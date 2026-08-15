using Microsoft.SqlServer.TransactSql.ScriptDom;
using Rtfq.Contracts;

namespace Rtfq.Adapters.SqlServer;

/// <summary>
/// The read guard for T-SQL, built on Microsoft's own ScriptDom.
///
/// Same shape as the PostgreSQL one — allow-list of statement types, exhaustive
/// walk, no string surgery — but the mechanics differ enough to be worth stating:
/// ScriptDom reports parse errors rather than guessing, and T-SQL permits several
/// statements in a batch with no separator at all, so the multi-statement check
/// matters more here than it does in Postgres.
///
/// The tree is walked with ScriptDom's own visitor, never reflection. ADR 0001
/// found that a reflection walk scores 20/20 under the JIT and 3/20 as a published
/// AOT binary, classifying DROP TABLE as a harmless read.
/// </summary>
public static class SqlServerReadGuard
{
    static readonly HashSet<Type> AllowedForRead = [typeof(SelectStatement)];

    public static GuardedRead Prepare(string sql, int? maxRows)
    {
        var parser = new TSql180Parser(initialQuotedIdentifiers: true);
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        if (errors.Count > 0)
            throw new AdapterException(ErrorCodes.SourceRejected, errors[0].Message);

        if (fragment is not TSqlScript script)
            throw new AdapterException(ErrorCodes.SourceRejected, "not a T-SQL script");

        var topLevel = script.Batches.SelectMany(b => b.Statements).ToList();
        if (topLevel.Count == 0)
            throw new AdapterException(ErrorCodes.StatementEmpty, "no statement to run");
        if (topLevel.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected,
                $"one statement per request; this is {topLevel.Count}");
        if (script.Batches.Count > 1)
            throw new AdapterException(ErrorCodes.SourceRejected, "GO separates this into multiple batches");

        var all = Descendants(script).OfType<TSqlStatement>().ToList();
        foreach (var statement in all)
        {
            if (AllowedForRead.Contains(statement.GetType())) continue;

            var reason = statement switch
            {
                InsertStatement or UpdateStatement or DeleteStatement or MergeStatement
                    => "this token has read access; writes arrive in M3",
                ExecuteStatement
                    => "EXEC runs a procedure and can carry dynamic SQL the parse tree cannot see",
                _ => $"{statement.GetType().Name} is not a read",
            };
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {reason}");
        }

        // SELECT ... INTO creates a table, exactly as in PostgreSQL.
        foreach (var select in Descendants(script).OfType<SelectStatement>())
        {
            if (select.Into is not null)
                throw new AdapterException(ErrorCodes.InsufficientAccess, "refused: SELECT INTO creates a relation");
        }

        if (maxRows is null) return new GuardedRead(sql, false);
        return ApplyTop(script, topLevel[0], sql, maxRows.Value);
    }

    /// <summary>
    /// T-SQL bounds a result with TOP rather than LIMIT, and TOP is part of the
    /// SELECT rather than a trailing clause — so this sets it on the query
    /// expression and regenerates the script, instead of appending text that a
    /// trailing comment could swallow.
    /// </summary>
    static GuardedRead ApplyTop(TSqlScript script, TSqlStatement statement, string original, int maxRows)
    {
        if (statement is not SelectStatement { QueryExpression: QuerySpecification query })
            return new GuardedRead(original, false);

        // Only tighten a literal we can read; anything else is left alone and the
        // scan-stop backstop still bounds the response.
        if (query.TopRowFilter is { } existing)
        {
            if (Literal(existing.Expression) is not { } current || current <= maxRows)
                return new GuardedRead(original, false);
        }

        query.TopRowFilter = new TopRowFilter
        {
            Expression = new IntegerLiteral { Value = maxRows.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        };

        var generator = new Sql170ScriptGenerator(new SqlScriptGeneratorOptions
        {
            KeywordCasing = KeywordCasing.Uppercase,
            IncludeSemicolons = false,
            NewLineBeforeFromClause = false,
        });

        generator.GenerateScript(script, out var rewritten);
        return string.IsNullOrWhiteSpace(rewritten)
            ? new GuardedRead(original, false)
            : new GuardedRead(rewritten.Trim(), true);
    }

    /// <summary>
    /// Reads an integer out of a TOP expression, unwrapping parentheses.
    ///
    /// <c>TOP (100)</c> is the idiomatic spelling and is mandatory in several
    /// contexts, and it parses as a ParenthesisExpression around the literal — so
    /// a check that only matched a bare IntegerLiteral silently declined to
    /// tighten every over-cap TOP written the normal way.
    /// </summary>
    static int? Literal(ScalarExpression? expression) => expression switch
    {
        IntegerLiteral literal when int.TryParse(literal.Value, out var value) => value,
        ParenthesisExpression parenthesis => Literal(parenthesis.Expression),
        _ => null,
    };

    /// <summary>
    /// ScriptDom's visitor, not reflection — see the class remarks.
    /// </summary>
    sealed class Collector : TSqlFragmentVisitor
    {
        public readonly List<TSqlFragment> Nodes = [];
        public override void Visit(TSqlFragment node) => Nodes.Add(node);
    }

    static IEnumerable<TSqlFragment> Descendants(TSqlFragment root)
    {
        var collector = new Collector();
        root.Accept(collector);
        return collector.Nodes;
    }
}
