using System.Text.Json;
using System.Text.Json.Nodes;
using Npgquery;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Postgres;

/// <summary>
/// The read half of the statement guard, arriving with M1 because LIMIT injection
/// needs a parse tree anyway.
///
/// It closes a real hole in 0.1.0: until now a read-granted token could send an
/// <c>UPDATE</c> through <c>query</c> and the server would run it, because policy
/// checked the <i>caller</i> and nothing checked the <i>statement</i>. Only the
/// database GRANT stood in the way.
///
/// Shape follows ADR 0001: an allow-list of statement node types, an exhaustive
/// walk rather than a top-level type switch, and the parser's own tree rather
/// than string surgery. The write half lands in M3.
/// </summary>
public static class PostgresReadGuard
{
    /// <summary>The only node types a read may contain. Everything else is refused by default.</summary>
    static readonly HashSet<string> AllowedForRead = ["SelectStmt", "ExplainStmt"];

    /// <summary>
    /// Validates a read and, when <paramref name="maxRows"/> is given, injects a
    /// row limit. Pass null to validate without rewriting — which is what
    /// <c>explain</c> needs, since a limit the caller did not write would change
    /// the plan they asked to see.
    /// </summary>
    /// <exception cref="AdapterException">The statement is not a plain read.</exception>
    public static GuardedRead Prepare(string sql, int? maxRows)
    {
        ParseResult parsed;
        try
        {
            parsed = Parser.QuickParse(sql, new ParseOptions());
        }
        catch (Exception ex)
        {
            throw new AdapterException(ErrorCodes.SourceRejected, $"could not parse statement: {ex.Message}", ex);
        }

        if (parsed.IsError)
            throw new AdapterException(ErrorCodes.SourceRejected, FirstLine(parsed.Error ?? "syntax error"));

        // A successful parse with no tree should be impossible; treat it as a
        // refusal rather than reasoning about a statement we cannot see.
        if (parsed.ParseTree is null)
            throw new AdapterException(ErrorCodes.SourceRejected, "statement produced no parse tree");

        var root = JsonNode.Parse(parsed.ParseTree.RootElement.GetRawText())?.AsObject()
                   ?? throw new AdapterException(ErrorCodes.Internal, "unreadable parse tree");

        if (root["stmts"] is not JsonArray statements || statements.Count == 0)
            throw new AdapterException(ErrorCodes.StatementEmpty, "no statement to run");

        if (statements.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected,
                $"one statement per request; this is {statements.Count}");

        // Checked before the node-type walk so the refusal names ANALYZE rather
        // than whatever statement it wraps: "EXPLAIN ANALYZE DELETE" is dangerous
        // because of the ANALYZE, and that is what the caller needs told.
        if (HasAnalyze(root))
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: EXPLAIN ANALYZE executes the statement; use plain EXPLAIN");

        // Exhaustive: a write buried in a CTE sits under a SelectStmt at the root.
        foreach (var nodeType in StatementNodeTypes(root))
        {
            if (AllowedForRead.Contains(nodeType)) continue;

            var reason = nodeType is "InsertStmt" or "UpdateStmt" or "DeleteStmt" or "MergeStmt"
                ? "this token has read access; writes arrive in M3"
                : $"{nodeType} is not a read";

            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {reason}");
        }

        // PostgreSQL SELECT INTO creates a table: DDL wearing a SelectStmt.
        if (ContainsKey(root, "intoClause"))
            throw new AdapterException(ErrorCodes.InsufficientAccess, "refused: SELECT INTO creates a relation");

        var stmt = statements[0]?["stmt"]?.AsObject();
        if (stmt is null) throw new AdapterException(ErrorCodes.Internal, "unreadable statement node");

        // EXPLAIN is built by the server, never accepted from a caller: that is
        // what keeps ANALYZE out of it structurally rather than by inspection.
        if (stmt.ContainsKey("ExplainStmt"))
            throw new AdapterException(ErrorCodes.SourceRejected, "use the explain endpoint rather than an EXPLAIN statement");

        if (maxRows is null) return new GuardedRead(sql, false);
        if (stmt["SelectStmt"] is not JsonObject select) return new GuardedRead(sql, false);

        return ApplyLimit(root, select, sql, maxRows.Value);
    }

    /// <summary>
    /// Injects or tightens the row limit in the tree, then deparses.
    ///
    /// Tree surgery rather than appending " LIMIT n" to the text: a statement
    /// ending in a line comment would swallow the appended clause, and a caller
    /// can produce one trivially.
    /// </summary>
    static GuardedRead ApplyLimit(JsonObject root, JsonObject select, string original, int maxRows)
    {
        var existing = select["limitCount"];

        if (existing is not null)
        {
            // Only tighten a literal we can read. An expression or parameter is
            // left alone; the scan-stop backstop still bounds the response.
            var literal = existing["A_Const"]?["ival"]?["ival"]?.GetValue<int>();
            if (literal is null || literal <= maxRows) return new GuardedRead(original, false);
        }

        select["limitCount"] = new JsonObject
        {
            ["A_Const"] = new JsonObject
            {
                ["ival"] = new JsonObject { ["ival"] = maxRows },
                ["location"] = -1,
            },
        };
        select["limitOption"] = "LIMIT_OPTION_COUNT";

        try
        {
            using var document = JsonDocument.Parse(root.ToJsonString());
            var deparsed = Parser.QuickDeparse(document);
            if (deparsed.IsError || string.IsNullOrWhiteSpace(deparsed.Query))
                return new GuardedRead(original, false);

            return new GuardedRead(deparsed.Query, true);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            // Injection is an optimisation; the cap is enforced by the scan-stop
            // regardless, so a deparse failure degrades rather than fails.
            return new GuardedRead(original, false);
        }
    }

    // --- tree helpers -------------------------------------------------------

    static IEnumerable<string> StatementNodeTypes(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key.EndsWith("Stmt", StringComparison.Ordinal) && char.IsUpper(key[0]))
                        yield return key;
                    foreach (var inner in StatementNodeTypes(value)) yield return inner;
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                    foreach (var inner in StatementNodeTypes(item)) yield return inner;
                break;
        }
    }

    static bool ContainsKey(JsonNode? node, string key) => node switch
    {
        JsonObject obj => obj.Any(p => (p.Key == key && p.Value is not null) || ContainsKey(p.Value, key)),
        JsonArray array => array.Any(item => ContainsKey(item, key)),
        _ => false,
    };

    static bool HasAnalyze(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p =>
            (p.Key == "DefElem" &&
             string.Equals(p.Value?["defname"]?.GetValue<string>(), "analyze", StringComparison.OrdinalIgnoreCase))
            || HasAnalyze(p.Value)),
        JsonArray array => array.Any(HasAnalyze),
        _ => false,
    };

    static string FirstLine(string text)
    {
        var i = text.IndexOf('\n', StringComparison.Ordinal);
        return i >= 0 ? text[..i] : text;
    }
}
