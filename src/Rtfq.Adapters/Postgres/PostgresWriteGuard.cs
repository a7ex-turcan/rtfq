using System.Text.Json;
using System.Text.Json.Nodes;
using Npgquery;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Postgres;

/// <summary>
/// The full PostgreSQL statement guard: reads, mutations and additive schema
/// changes.
///
/// Shape follows ADR 0001 — an allow-list of statement node types, an exhaustive
/// walk rather than a top-level type switch, and the parser's own tree rather
/// than string surgery — and ADR 0002 for DDL, where the allow-list needs a
/// <b>second level</b> because ADD COLUMN and DROP COLUMN are the same node type.
/// </summary>
public static class PostgresWriteGuard
{
    static readonly HashSet<string> ReadNodes = ["SelectStmt"];
    static readonly HashSet<string> MutationNodes = ["InsertStmt", "UpdateStmt", "DeleteStmt", "MergeStmt"];
    static readonly HashSet<string> SchemaNodes = ["AlterTableStmt", "IndexStmt", "DropStmt"];

    /// <summary>
    /// ALTER TABLE subcommands that are additive or corrective. Anything absent —
    /// AT_DropColumn most of all — is refused (ADR 0002).
    /// </summary>
    static readonly HashSet<string> AllowedAlterSubtypes =
    [
        "AT_AddColumn", "AT_AlterColumnType", "AT_AddConstraint", "AT_DropConstraint",
        "AT_SetNotNull", "AT_DropNotNull", "AT_ColumnDefault",
    ];

    public static GuardedStatement Prepare(string sql, int? maxRows)
    {
        var root = Parse(sql);
        return Prepare(sql, root, maxRows);
    }

    static GuardedStatement Prepare(string sql, JsonObject root, int? maxRows)
    {
        var statements = root["stmts"] as JsonArray
                         ?? throw new AdapterException(ErrorCodes.StatementEmpty, "no statement to run");

        if (statements.Count == 0)
            throw new AdapterException(ErrorCodes.StatementEmpty, "no statement to run");
        if (statements.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected, $"one statement per request; this is {statements.Count}");

        if (HasAnalyze(root))
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: EXPLAIN ANALYZE executes the statement; use plain EXPLAIN");

        var present = StatementNodeTypes(root).ToHashSet(StringComparer.Ordinal);
        foreach (var node in present)
        {
            if (ReadNodes.Contains(node) || MutationNodes.Contains(node) || SchemaNodes.Contains(node)) continue;
            if (node == "ExplainStmt")
                throw new AdapterException(ErrorCodes.SourceRejected, "use the explain endpoint rather than an EXPLAIN statement");

            // COPY ... FROM PROGRAM, DO, GRANT, SET ROLE, TRUNCATE, CREATE TABLE:
            // none is DML and none is on the list, so none runs.
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {node} is not permitted");
        }

        if (ContainsKey(root, "intoClause"))
            throw new AdapterException(ErrorCodes.InsufficientAccess, "refused: SELECT INTO creates a relation");

        var referenced = Relations(root);
        var body = statements[0]?["stmt"]?.AsObject()
                   ?? throw new AdapterException(ErrorCodes.Internal, "unreadable statement node");

        if (present.Overlaps(SchemaNodes)) return Schema(sql, body, present, referenced);
        if (present.Overlaps(MutationNodes)) return Mutation(root, body, present, referenced);

        return Read(sql, root, body, referenced, maxRows);
    }

    // --- reads ---------------------------------------------------------------

    /// <summary>
    /// Injects or tightens the row limit in the tree, then deparses.
    ///
    /// Tree surgery rather than appending " LIMIT n": a statement ending in a line
    /// comment would swallow the appended clause, and a caller can produce one
    /// trivially. When nothing needs changing the ORIGINAL text is returned, so an
    /// untouched statement is not silently reformatted.
    /// </summary>
    static GuardedStatement Read(string sql, JsonObject root, JsonObject body, List<string> referenced, int? maxRows)
    {
        var unchanged = new GuardedStatement
        {
            Kind = StatementKind.Read,
            Statement = sql,
            Referenced = referenced,
        };

        if (maxRows is null || body["SelectStmt"] is not JsonObject select) return unchanged;

        if (select["limitCount"] is { } existing)
        {
            // Only tighten a literal we can read. An expression or parameter is
            // left alone; the scan-stop backstop still bounds the response.
            var literal = existing["A_Const"]?["ival"]?["ival"]?.GetValue<int>();
            if (literal is null || literal <= maxRows) return unchanged;
        }

        select["limitCount"] = new JsonObject
        {
            ["A_Const"] = new JsonObject
            {
                ["ival"] = new JsonObject { ["ival"] = maxRows.Value },
                ["location"] = -1,
            },
        };
        select["limitOption"] = "LIMIT_OPTION_COUNT";

        try
        {
            return unchanged with { Statement = Deparse(root), Rewritten = true };
        }
        catch (AdapterException)
        {
            // Injection is an optimisation; the cap is enforced by the scan-stop
            // regardless, so a deparse failure degrades rather than fails.
            return unchanged;
        }
    }

    // --- mutations --------------------------------------------------------------

    static GuardedStatement Mutation(JsonObject root, JsonObject body, HashSet<string> present, List<string> referenced)
    {
        // The write must BE the statement, not sit inside one. A DELETE in a CTE
        // under a top-level SELECT has no single unambiguous target, so the
        // target allow-list and the affected-row cap would both be guessing —
        // and a gate that guesses is not a gate.
        var node = body.Select(p => p.Key).FirstOrDefault(MutationNodes.Contains);
        if (node is null)
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: a write nested inside another statement cannot be gated; issue it as the statement itself");

        if (present.Count(MutationNodes.Contains) > 1)
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: more than one write in a single statement");

        var statement = body[node]?.AsObject()
                        ?? throw new AdapterException(ErrorCodes.Internal, "unreadable mutation node");

        var target = statement["relation"] is JsonObject relation
            ? RangeVarName(relation)
            : throw new AdapterException(ErrorCodes.SourceRejected, "could not resolve the mutation target");

        string? beforeImages = null;

        if (node is "UpdateStmt" or "DeleteStmt")
        {
            var where = statement["whereClause"];
            if (where is null)
                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    $"refused: {node} has no WHERE clause. There is no override for this.");

            if (TriviallyTrue(where))
                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    "refused: the WHERE clause is trivially true, which is unqualified however it is spelled");

            // UPDATE ... FROM and DELETE ... USING bring extra relations into
            // scope, and the predicate refers to them. A before-image query built
            // from the target alone would reference an alias it never declared.
            var extra = (statement["fromClause"] ?? statement["usingClause"]) as JsonArray;
            beforeImages = BeforeImageQuery(statement["relation"]!, where, extra);
        }

        return new GuardedStatement
        {
            Kind = StatementKind.Mutation,
            Statement = Deparse(root),
            Target = target,
            Referenced = referenced,
            BeforeImageQuery = beforeImages,
        };
    }

    /// <summary>
    /// Builds <c>SELECT * FROM target WHERE predicate</c> from the tree, so the
    /// rows journalled are exactly the rows the mutation is about to change.
    ///
    /// Assembled as a parse tree and deparsed rather than concatenated: the
    /// predicate can contain anything, and re-serialising it through the parser
    /// is the only way to be sure it means the same thing in its new position.
    /// </summary>
    static string BeforeImageQuery(JsonNode relation, JsonNode where, JsonArray? extraFrom)
    {
        // Parsed from a template rather than hand-built. A tree assembled by hand
        // is missing fields the deparser expects and fails to render; letting the
        // parser produce the skeleton means only the two interesting nodes are
        // swapped in, and both come from a tree the parser already built.
        var tree = Parse("SELECT * FROM rtfq_before_image_placeholder WHERE true");

        var select = tree["stmts"]?[0]?["stmt"]?["SelectStmt"]?.AsObject()
                     ?? throw new AdapterException(ErrorCodes.Internal, "could not build the before-image query");

        // fromClause holds Node*, so each entry is wrapped as {"RangeVar": {...}}.
        // UpdateStmt.relation is typed RangeVar* and so is stored unwrapped. Moving
        // one into the other without re-wrapping is what makes the deparser report
        // "Unknown field: relname".
        var from = new JsonArray(new JsonObject { ["RangeVar"] = relation.DeepClone() });
        foreach (var entry in extraFrom ?? []) from.Add(entry?.DeepClone());

        select["fromClause"] = from;
        select["whereClause"] = where.DeepClone();

        return Deparse(tree);
    }

    // --- schema changes -------------------------------------------------------------

    static GuardedStatement Schema(string sql, JsonObject body, HashSet<string> present, List<string> referenced)
    {
        var node = SchemaNodes.First(present.Contains);
        var statement = body[node]?.AsObject()
                        ?? throw new AdapterException(ErrorCodes.Internal, "unreadable schema node");

        switch (node)
        {
            case "DropStmt":
            {
                // DROP INDEX is in scope; DROP anything else destroys data.
                var what = statement["removeType"]?.GetValue<string>();
                if (what != "OBJECT_INDEX")
                    throw new AdapterException(ErrorCodes.InsufficientAccess,
                        $"refused: DROP of {what ?? "that object"} destroys data");

                return SchemaResult(sql, referenced, target: "", summary: "drop index");
            }

            case "IndexStmt":
            {
                if (statement["concurrent"]?.GetValue<bool>() == true)
                    throw new AdapterException(ErrorCodes.InsufficientAccess,
                        "refused: CREATE INDEX CONCURRENTLY cannot run inside a transaction, so propose/commit cannot cover it");

                var relation = statement["relation"] is JsonObject r ? RangeVarName(r) : "";
                return SchemaResult(sql, referenced, relation, "create index");
            }

            case "AlterTableStmt":
            {
                if (statement["cmds"] is not JsonArray commands || commands.Count == 0)
                    throw new AdapterException(ErrorCodes.SourceRejected, "ALTER TABLE with no readable subcommand");

                foreach (var command in commands)
                {
                    var cmd = command?["AlterTableCmd"]?.AsObject()
                              ?? throw new AdapterException(ErrorCodes.SourceRejected, "unreadable ALTER TABLE subcommand");

                    var subtype = cmd["subtype"]?.GetValue<string>();
                    if (subtype is null || !AllowedAlterSubtypes.Contains(subtype))
                        throw new AdapterException(ErrorCodes.InsufficientAccess,
                            $"refused: {subtype ?? "that subcommand"} is not additive or corrective");

                    // ALTER COLUMN ... TYPE ... USING runs an arbitrary transform
                    // over every row: corrective-looking, silently destructive.
                    if (subtype == "AT_AlterColumnType" && ContainsKey(cmd, "raw_default"))
                        throw new AdapterException(ErrorCodes.InsufficientAccess,
                            "refused: ALTER COLUMN TYPE ... USING is an arbitrary transform over every row");
                }

                var target = statement["relation"] is JsonObject rel ? RangeVarName(rel) : "";
                var summary = string.Join(", ", commands
                    .Select(c => c?["AlterTableCmd"]?["subtype"]?.GetValue<string>())
                    .Where(s => s is not null));

                return SchemaResult(sql, referenced, target, summary);
            }
        }

        throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {node} is not permitted");
    }

    static GuardedStatement SchemaResult(string sql, List<string> referenced, string target, string summary) => new()
    {
        Kind = StatementKind.Schema,
        // Schema statements execute verbatim. Deparsing them would gain nothing
        // and risks the round-trip changing something subtle.
        Statement = sql,
        Target = target,
        Referenced = referenced,
        SchemaSummary = summary,
    };

    // --- tree helpers ------------------------------------------------------------

    internal static JsonObject Parse(string sql)
    {
        ParseResult parsed;
        try { parsed = Parser.QuickParse(sql, new ParseOptions()); }
        catch (Exception ex)
        {
            throw new AdapterException(ErrorCodes.SourceRejected, $"could not parse statement: {ex.Message}", ex);
        }

        if (parsed.IsError)
            throw new AdapterException(ErrorCodes.SourceRejected, FirstLine(parsed.Error ?? "syntax error"));
        if (parsed.ParseTree is null)
            throw new AdapterException(ErrorCodes.SourceRejected, "statement produced no parse tree");

        return JsonNode.Parse(parsed.ParseTree.RootElement.GetRawText())?.AsObject()
               ?? throw new AdapterException(ErrorCodes.Internal, "unreadable parse tree");
    }

    internal static string Deparse(JsonObject tree)
    {
        using var document = JsonDocument.Parse(tree.ToJsonString());
        var deparsed = Parser.QuickDeparse(document);

        if (deparsed.IsError || string.IsNullOrWhiteSpace(deparsed.Query))
        {
            throw new AdapterException(ErrorCodes.Internal,
                $"could not render the statement back to SQL: {deparsed.Error ?? "(no detail)"}");
        }

        return deparsed.Query;
    }

    internal static IEnumerable<string> StatementNodeTypes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.EndsWith("Stmt", StringComparison.Ordinal) && char.IsUpper(key[0])) yield return key;
                    foreach (var inner in StatementNodeTypes(value)) yield return inner;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var inner in StatementNodeTypes(item)) yield return inner;
                break;
        }
    }

    /// <summary>Every relation the statement mentions, so deny rules can apply to all of them.</summary>
    internal static List<string> Relations(JsonNode? node)
    {
        var found = new List<string>();
        Walk(node);
        return found;

        void Walk(JsonNode? current)
        {
            switch (current)
            {
                case JsonObject obj:
                    foreach (var (key, value) in obj)
                    {
                        // "RangeVar" is the wrapped form used in a Node* list;
                        // "relation" is the unwrapped form used where the field is
                        // typed RangeVar*, as on UpdateStmt and DeleteStmt. Missing
                        // the second would leave a mutation's own target out of the
                        // list the deny rules are applied to.
                        if (key is "RangeVar" or "relation" && value is JsonObject rangeVar)
                        {
                            var name = RangeVarName(rangeVar);
                            if (name.Length > 0 && !found.Contains(name, StringComparer.Ordinal)) found.Add(name);
                        }
                        Walk(value);
                    }
                    break;
                case JsonArray array:
                    foreach (var item in array) Walk(item);
                    break;
            }
        }
    }

    /// <summary>
    /// Renders schema.relation. An absent schema is NOT reliably "public" — it
    /// resolves through search_path at execution time — which is why the adapter
    /// pins search_path on every connection.
    /// </summary>
    internal static string RangeVarName(JsonObject rangeVar)
    {
        string? Get(string key) => rangeVar[key]?.GetValue<string>();

        var relation = Get("relname") ?? "";
        if (relation.Length == 0) return "";

        var schema = Get("schemaname") ?? "public";
        var catalog = Get("catalogname");
        return catalog is null ? $"{schema}.{relation}" : $"{catalog}.{schema}.{relation}";
    }

    internal static bool ContainsKey(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.Any(p => (p.Key == key && p.Value is not null) || ContainsKey(p.Value, key)),
        JsonArray array => array.Any(item => ContainsKey(item, key)),
        _ => false,
    };

    internal static bool HasAnalyze(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p =>
            (p.Key == "DefElem" &&
             string.Equals(p.Value?["defname"]?.GetValue<string>(), "analyze", StringComparison.OrdinalIgnoreCase))
            || HasAnalyze(p.Value)),
        JsonArray array => array.Any(HasAnalyze),
        _ => false,
    };

    /// <summary>WHERE true, WHERE 1=1, and any OR-branch reducing to either.</summary>
    internal static bool TriviallyTrue(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;

        if (obj["A_Const"]?["boolval"]?["boolval"]?.GetValue<bool>() == true) return true;

        if (obj["A_Expr"] is JsonObject expr && OperatorName(expr) == "=" && SameConstant(expr))
            return true;

        if (obj["BoolExpr"] is JsonObject boolExpr && boolExpr["args"] is JsonArray args)
        {
            return boolExpr["boolop"]?.GetValue<string>() switch
            {
                "OR_EXPR" => args.Any(TriviallyTrue),
                "AND_EXPR" => args.Count > 0 && args.All(TriviallyTrue),
                _ => false,
            };
        }
        return false;
    }

    static string OperatorName(JsonObject expr)
    {
        if (expr["name"] is not JsonArray names) return "";
        foreach (var name in names)
        {
            if (name?["String"]?["sval"]?.GetValue<string>() is { } value) return value;
        }
        return "";
    }

    static bool SameConstant(JsonObject expr)
    {
        var left = Normalise(expr["lexpr"]);
        var right = Normalise(expr["rexpr"]);
        return left.Contains("A_Const", StringComparison.Ordinal) && left == right;
    }

    static string Normalise(JsonNode? node) => node switch
    {
        JsonObject obj => "{" + string.Join(",", obj
            .Where(p => p.Key != "location")
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"\"{p.Key}\":{Normalise(p.Value)}")) + "}",
        JsonArray array => "[" + string.Join(",", array.Select(Normalise)) + "]",
        null => "null",
        _ => node.ToJsonString(),
    };

    static string FirstLine(string text)
    {
        var i = text.IndexOf('\n', StringComparison.Ordinal);
        return i >= 0 ? text[..i] : text;
    }
}
