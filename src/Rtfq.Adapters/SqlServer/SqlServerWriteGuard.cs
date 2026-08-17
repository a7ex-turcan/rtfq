using Microsoft.SqlServer.TransactSql.ScriptDom;
using Rtfq.Contracts;

namespace Rtfq.Adapters.SqlServer;

/// <summary>
/// The full T-SQL guard: reads, mutations and additive schema changes.
///
/// Same two-level shape as PostgreSQL's, but the levels fall differently. T-SQL
/// splits ADD, ALTER and DROP of a table element into three statement types, so
/// the interesting distinction moves down one: DROP COLUMN and DROP CONSTRAINT
/// share <see cref="AlterTableDropTableElementStatement"/> and are separated only
/// by the element kind. Getting that wrong drops a column (ADR 0002).
/// </summary>
public static class SqlServerWriteGuard
{
    public static GuardedStatement Prepare(string sql, int? maxRows)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0) throw new AdapterException(ErrorCodes.SourceRejected, errors[0].Message);
        if (fragment is not TSqlScript script)
            throw new AdapterException(ErrorCodes.SourceRejected, "not a T-SQL script");

        var topLevel = script.Batches.SelectMany(b => b.Statements).ToList();
        if (topLevel.Count == 0) throw new AdapterException(ErrorCodes.StatementEmpty, "no statement to run");
        if (topLevel.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected, $"one statement per request; this is {topLevel.Count}");
        if (script.Batches.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected, "GO separates this into multiple batches");

        var all = Descendants(script).OfType<TSqlStatement>().ToList();
        var referenced = Descendants(script).OfType<SchemaObjectName>().Select(Render)
            .Where(n => n.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        foreach (var nested in all.Skip(1))
        {
            // Anything nested inside the statement is a construct we cannot gate
            // — a write in an IF, a batch in a BEGIN block.
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {nested.GetType().Name} nested inside another statement cannot be gated");
        }

        var statement = topLevel[0];

        foreach (var select in Descendants(script).OfType<SelectStatement>())
        {
            if (select.Into is not null)
                throw new AdapterException(ErrorCodes.InsufficientAccess, "refused: SELECT INTO creates a relation");
        }

        return statement switch
        {
            SelectStatement select => Read(select, script, sql, referenced, maxRows),
            UpdateStatement or DeleteStatement or InsertStatement or MergeStatement
                => Mutation(statement, sql, referenced),
            _ => Schema(statement, sql, referenced),
        };
    }

    // --- reads ----------------------------------------------------------------

    static GuardedStatement Read(
        SelectStatement select, TSqlScript script, string sql, List<string> referenced, int? maxRows)
    {
        var unchanged = new GuardedStatement { Kind = StatementKind.Read, Statement = sql, Referenced = referenced };
        if (maxRows is null || select.QueryExpression is not QuerySpecification query) return unchanged;

        if (query.TopRowFilter is { } existing)
        {
            if (Literal(existing.Expression) is not { } current || current <= maxRows) return unchanged;
        }

        query.TopRowFilter = new TopRowFilter
        {
            Expression = new IntegerLiteral { Value = maxRows.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = false,
        });
        generator.GenerateScript(script, out var rewritten);

        return string.IsNullOrWhiteSpace(rewritten)
            ? unchanged
            : unchanged with { Statement = rewritten.Trim(), Rewritten = true };
    }

    // --- mutations ---------------------------------------------------------------

    static GuardedStatement Mutation(TSqlStatement statement, string sql, List<string> referenced)
    {
        var (target, where, from) = statement switch
        {
            UpdateStatement u => (Target(u.UpdateSpecification.Target), u.UpdateSpecification.WhereClause,
                                  u.UpdateSpecification.FromClause),
            DeleteStatement d => (Target(d.DeleteSpecification.Target), d.DeleteSpecification.WhereClause,
                                  d.DeleteSpecification.FromClause),
            InsertStatement i => (Target(i.InsertSpecification.Target), null, null),
            MergeStatement m => (Target(m.MergeSpecification.Target), null, null),
            _ => ("", null, null),
        };

        if (target.Length == 0)
            throw new AdapterException(ErrorCodes.SourceRejected, "could not resolve the mutation target");

        if (statement is InsertStatement insert && insert.InsertSpecification.InsertSource is ExecuteInsertSource)
        {
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: INSERT ... EXEC runs a stored procedure behind an allow-listed target");
        }

        string? beforeImages = null;

        if (statement is UpdateStatement or DeleteStatement)
        {
            if (where is null)
                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    "refused: no WHERE clause. There is no override for this.");

            if (TriviallyTrue(where.SearchCondition))
                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    "refused: the WHERE clause is trivially true, which is unqualified however it is spelled");

            beforeImages = BeforeImageQuery(statement, where, from);
        }

        return new GuardedStatement
        {
            Kind = StatementKind.Mutation,
            Statement = sql,
            Target = target,
            Referenced = referenced,
            BeforeImageQuery = beforeImages,
        };
    }

    /// <summary>
    /// Builds a SELECT over the rows about to change, assembled from the parsed
    /// fragments and regenerated by ScriptDom rather than concatenated.
    /// </summary>
    static string BeforeImageQuery(TSqlStatement statement, WhereClause where, FromClause? from)
    {
        var query = new QuerySpecification
        {
            WhereClause = where,
            FromClause = from ?? SingleTableFrom(statement),
        };
        query.SelectElements.Add(new SelectStarExpression());

        var select = new SelectStatement { QueryExpression = query };
        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = false,
        });

        generator.GenerateScript(select, out var sql);
        return sql.Trim();
    }

    /// <summary>
    /// An UPDATE with no FROM implies its own target, which the generated SELECT
    /// has to state explicitly.
    /// </summary>
    static FromClause SingleTableFrom(TSqlStatement statement)
    {
        var reference = statement switch
        {
            UpdateStatement u => u.UpdateSpecification.Target,
            DeleteStatement d => d.DeleteSpecification.Target,
            _ => null,
        };

        var from = new FromClause();
        if (reference is not null) from.TableReferences.Add(reference);
        return from;
    }

    // --- schema changes ---------------------------------------------------------------

    static GuardedStatement Schema(TSqlStatement statement, string sql, List<string> referenced)
    {
        var (target, summary) = statement switch
        {
            AlterTableAddTableElementStatement add => (Render(add.SchemaObjectName), "add element"),
            AlterTableAlterColumnStatement alter => (Render(alter.SchemaObjectName), "alter column"),

            // The case a statement-type allow-list gets wrong: this one type covers
            // dropping a column, which destroys data, and dropping a constraint,
            // which does not.
            AlterTableDropTableElementStatement drop
                when drop.AlterTableDropTableElements.Any(e => e.TableElementType == TableElementType.Column)
                => throw new AdapterException(ErrorCodes.InsufficientAccess,
                    "refused: DROP COLUMN destroys a column's data while affecting zero rows"),

            AlterTableDropTableElementStatement drop => (Render(drop.SchemaObjectName), "drop constraint"),

            CreateIndexStatement create => (Render(create.OnName), "create index"),
            DropIndexStatement => ("", "drop index"),

            DropTableStatement => throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: DROP TABLE destroys everything in it"),
            TruncateTableStatement => throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: TRUNCATE has no affected-row count to cap"),
            CreateTableStatement => throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: CREATE TABLE is a deploy, not a repair"),
            AlterSchemaStatement => throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: ALTER SCHEMA ... TRANSFER changes what an allow-list entry resolves to"),
            ExecuteStatement => throw new AdapterException(ErrorCodes.InsufficientAccess,
                "refused: EXEC runs a procedure and can carry dynamic SQL the parse tree cannot see"),

            _ => throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {statement.GetType().Name} is not permitted"),
        };

        return new GuardedStatement
        {
            Kind = StatementKind.Schema,
            Statement = sql,
            Target = target,
            Referenced = referenced,
            SchemaSummary = summary,
        };
    }

    // --- helpers ------------------------------------------------------------------------

    static string Target(TableReference? reference) =>
        reference is null ? "" : Descendants(reference).OfType<SchemaObjectName>().Select(Render).FirstOrDefault() ?? "";

    static string Render(SchemaObjectName? name)
    {
        if (name?.BaseIdentifier?.Value is not { } baseId || baseId.Length == 0) return "";
        var schema = name.SchemaIdentifier?.Value ?? "dbo";
        var database = name.DatabaseIdentifier?.Value;
        return database is null ? $"{schema}.{baseId}" : $"{database}.{schema}.{baseId}";
    }

    static bool TriviallyTrue(BooleanExpression? e) => e switch
    {
        BooleanParenthesisExpression p => TriviallyTrue(p.Expression),
        BooleanComparisonExpression c when c.ComparisonType == BooleanComparisonType.Equals
            => SameLiteral(c.FirstExpression, c.SecondExpression),
        BooleanBinaryExpression b when b.BinaryExpressionType == BooleanBinaryExpressionType.Or
            => TriviallyTrue(b.FirstExpression) || TriviallyTrue(b.SecondExpression),
        BooleanBinaryExpression b when b.BinaryExpressionType == BooleanBinaryExpressionType.And
            => TriviallyTrue(b.FirstExpression) && TriviallyTrue(b.SecondExpression),
        _ => false,
    };

    static bool SameLiteral(ScalarExpression a, ScalarExpression b) =>
        a is Literal la && b is Literal lb && la.GetType() == lb.GetType() && la.Value == lb.Value;

    static int? Literal(ScalarExpression? expression) => expression switch
    {
        IntegerLiteral literal when int.TryParse(literal.Value, out var value) => value,
        ParenthesisExpression parenthesis => Literal(parenthesis.Expression),
        _ => null,
    };

    sealed class Collector : TSqlFragmentVisitor
    {
        public readonly List<TSqlFragment> Nodes = [];
        public override void Visit(TSqlFragment node) => Nodes.Add(node);
    }

    // ScriptDom's own visitor, never reflection: a reflection walk scores 20/20
    // under the JIT and 3/20 as a published AOT binary (ADR 0001).
    static IEnumerable<TSqlFragment> Descendants(TSqlFragment root)
    {
        var collector = new Collector();
        root.Accept(collector);
        return collector.Nodes;
    }
}
