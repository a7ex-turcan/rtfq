using System.Collections;
using System.Diagnostics;
using System.Reflection;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace Rtfq.Spike;

/// <summary>
/// Runs the T-SQL corpus through Microsoft.SqlServer.TransactSql.ScriptDom -
/// Microsoft's own first-party T-SQL parser, the same one SSMS and SqlPackage use.
///
/// Guard shape mirrors the Go spike: allow-list of statement types, exhaustive
/// walk, predicate analysis rather than a WHERE-presence test.
/// </summary>
public static class TSqlSpike
{
    static readonly HashSet<Type> Allowed =
    [
        typeof(SelectStatement),
        typeof(InsertStatement),
        typeof(UpdateStatement),
        typeof(DeleteStatement),
        typeof(MergeStatement),
    ];

    static readonly HashSet<Type> Mutating =
    [
        typeof(InsertStatement),
        typeof(UpdateStatement),
        typeof(DeleteStatement),
        typeof(MergeStatement),
    ];

    // DDL the guard will even consider. Note how differently T-SQL carves this up
    // from PostgreSQL: ADD, ALTER and DROP of a table element are three distinct
    // statement types here, where libpg_query has one AlterTableStmt with a
    // subcommand list. The adapter absorbs that difference; the policy does not.
    static readonly HashSet<Type> DdlTypes =
    [
        typeof(AlterTableAddTableElementStatement),
        typeof(AlterTableAlterColumnStatement),
        typeof(AlterTableDropTableElementStatement),
        typeof(CreateIndexStatement),
        typeof(DropIndexStatement),
        typeof(DropTableStatement),
        typeof(TruncateTableStatement),
        typeof(CreateTableStatement),
        typeof(AlterSchemaStatement),
    ];

    record Outcome(Verdict Verdict, string Target, string Detail);

    public static int Run()
    {
        var (parser, parserName) = MakeParser();
        Console.WriteLine($"=== SQL Server: Microsoft ScriptDom ({parserName}) ===");
        Console.WriteLine();

        int fail = 0;
        fail += RunCorpus(parser, "DML and reads", Corpus.TSql);
        fail += RunCorpus(parser, "DDL: additive and corrective only", Corpus.TSqlDdl);

        FailClosedProbe(parser);
        Latency(parser);
        return fail > 0 ? 1 : 0;
    }

    static int RunCorpus(TSqlParser parser, string title, Case[] cases)
    {
        Console.WriteLine($"-- {title} --");
        int pass = 0, fail = 0;
        var failures = new List<string>();

        foreach (var c in cases)
        {
            var got = Classify(parser, c.Sql);
            bool ok = got.Verdict == c.Want;
            bool targetOk = !ok || c.Target.Length == 0 || got.Target == c.Target;

            if (ok && targetOk) pass++;
            else
            {
                fail++;
                failures.Add($"{c.Name,-22} want={c.Want,-8} got={got.Verdict,-8} target want=\"{c.Target}\" got=\"{got.Target}\" ({got.Detail})");
            }
            Console.WriteLine($"{(ok && targetOk ? "PASS" : "FAIL")}  {c.Name,-22} {got.Verdict,-8} {got.Target,-24} {got.Detail}");
        }

        Console.WriteLine($"RESULT: {pass} passed, {fail} failed, {cases.Length} total");
        if (failures.Count > 0)
        {
            Console.WriteLine("FAILURES:");
            foreach (var f in failures) Console.WriteLine("  " + f);
        }
        Console.WriteLine();
        return fail;
    }

    // --- classification ---------------------------------------------------

    static Outcome Classify(TSqlParser parser, string sql)
    {
        using var reader = new StringReader(sql);
        var fragment = parser.Parse(reader, out IList<ParseError> errors);

        // ScriptDom reports errors rather than guessing. Fail closed.
        if (errors.Count > 0)
            return new(Verdict.Reject, "", $"parse error: {errors[0].Message}");
        if (fragment is not TSqlScript script)
            return new(Verdict.Reject, "", "not a script");

        var batches = script.Batches;
        var topLevel = batches.SelectMany(b => b.Statements).ToList();
        if (topLevel.Count == 0)
            return new(Verdict.Reject, "", "no statement");
        if (topLevel.Count > 1)
            return new(Verdict.Reject, "", $"multi-statement ({topLevel.Count})");
        if (batches.Count > 1)
            return new(Verdict.Reject, "", $"multiple batches ({batches.Count})");

        // Exhaustive: a nested statement anywhere disqualifies.
        var all = Descendants(script).OfType<TSqlStatement>().ToList();

        // DDL takes its own path: the row cap and before-images do not apply to it.
        var ddl = all.Where(s => DdlTypes.Contains(s.GetType())).ToList();
        if (ddl.Count > 0)
        {
            if (ddl.Count > 1 || all.Count > ddl.Count)
                return new(Verdict.Reject, "", "mixed DDL and DML");
            return ClassifyDdl(ddl[0]);
        }

        foreach (var s in all)
            if (!Allowed.Contains(s.GetType()))
                return new(Verdict.Reject, "", $"disallowed node: {s.GetType().Name}");

        // INSERT ... EXEC runs a stored procedure behind an allow-listed INSERT.
        foreach (var ins in Descendants(script).OfType<InsertSpecification>())
            if (ins.InsertSource is ExecuteInsertSource)
                return new(Verdict.Reject, "", "InsertSource is ExecuteInsertSource");

        // SELECT ... INTO creates a table.
        foreach (var sel in Descendants(script).OfType<SelectStatement>())
            if (sel.Into is not null)
                return new(Verdict.Reject, "", "SELECT INTO creates a relation");

        bool isMutation = all.Any(s => Mutating.Contains(s.GetType()));
        if (!isMutation)
            return new(Verdict.Read, FirstTable(script), "read");

        foreach (var s in all)
        {
            BooleanExpression? where = s switch
            {
                UpdateStatement u => u.UpdateSpecification.WhereClause?.SearchCondition,
                DeleteStatement d => d.DeleteSpecification.WhereClause?.SearchCondition,
                _ => null
            };
            bool needsWhere = s is UpdateStatement or DeleteStatement;
            if (!needsWhere) continue;
            if (where is null)
                return new(Verdict.Reject, "", "unqualified mutation");
            if (TriviallyTrue(where))
                return new(Verdict.Reject, "", "trivially-true predicate");
        }

        return new(Verdict.Mutation, WriteTarget(all), "bounded mutation");
    }

    /// <summary>
    /// Additive and corrective schema change only. Refused outright: anything that
    /// destroys data, and anything that changes what a write allow-list entry
    /// resolves to.
    /// </summary>
    static Outcome ClassifyDdl(TSqlStatement stmt) => stmt switch
    {
        AlterTableAddTableElementStatement add
            => new(Verdict.Schema, FirstTable(add.SchemaObjectName), "additive"),

        AlterTableAlterColumnStatement alter
            => new(Verdict.Schema, FirstTable(alter.SchemaObjectName), "corrective"),

        // One statement type covers DROP COLUMN and DROP CONSTRAINT; only the
        // element kind separates destroying data from removing a rule.
        AlterTableDropTableElementStatement drop
            => drop.AlterTableDropTableElements.Any(e => e.TableElementType == TableElementType.Column)
                ? new(Verdict.Reject, "", "DROP COLUMN destroys a column's data")
                : new(Verdict.Schema, FirstTable(drop.SchemaObjectName), "drops a constraint/index, not data"),

        CreateIndexStatement ci => new(Verdict.Schema, FirstTable(ci.OnName), "create index"),

        DropIndexStatement di => new(Verdict.Schema, DropIndexTarget(di), "drop index"),

        DropTableStatement => new(Verdict.Reject, "", "DROP TABLE destroys everything"),
        TruncateTableStatement => new(Verdict.Reject, "", "TRUNCATE has no affected-row count to cap"),
        CreateTableStatement => new(Verdict.Reject, "", "CREATE TABLE is a deploy, not a repair"),
        AlterSchemaStatement => new(Verdict.Reject, "", "ALTER SCHEMA ... TRANSFER rewrites what the allow-list refers to"),

        _ => new(Verdict.Reject, "", "unhandled DDL node: " + stmt.GetType().Name)
    };

    static string FirstTable(SchemaObjectName? n) => n is null ? "" : Render(n);

    static string DropIndexTarget(DropIndexStatement di)
    {
        foreach (var clause in di.DropIndexClauses)
        {
            var name = Descendants(clause).OfType<SchemaObjectName>().FirstOrDefault();
            if (name is not null) return Render(name);
        }
        return "";
    }

    static bool TriviallyTrue(BooleanExpression e) => e switch
    {
        BooleanParenthesisExpression p => TriviallyTrue(p.Expression),
        BooleanComparisonExpression c when c.ComparisonType == BooleanComparisonType.Equals
            => SameLiteral(c.FirstExpression, c.SecondExpression),
        BooleanBinaryExpression b when b.BinaryExpressionType == BooleanBinaryExpressionType.Or
            => TriviallyTrue(b.FirstExpression) || TriviallyTrue(b.SecondExpression),
        BooleanBinaryExpression b when b.BinaryExpressionType == BooleanBinaryExpressionType.And
            => TriviallyTrue(b.FirstExpression) && TriviallyTrue(b.SecondExpression),
        _ => false
    };

    static bool SameLiteral(ScalarExpression a, ScalarExpression b) =>
        a is Literal la && b is Literal lb && la.GetType() == lb.GetType() && la.Value == lb.Value;

    static string WriteTarget(IEnumerable<TSqlStatement> stmts)
    {
        foreach (var s in stmts)
        {
            TableReference? target = s switch
            {
                UpdateStatement u => u.UpdateSpecification.Target,
                DeleteStatement d => d.DeleteSpecification.Target,
                InsertStatement i => i.InsertSpecification.Target,
                MergeStatement m => m.MergeSpecification.Target,
                _ => null
            };
            if (target is null) continue;
            var name = Descendants(target).OfType<SchemaObjectName>().FirstOrDefault();
            if (name is not null) return Render(name);
        }
        return "";
    }

    static string FirstTable(TSqlFragment root)
    {
        var n = Descendants(root).OfType<SchemaObjectName>().FirstOrDefault();
        return n is null ? "" : Render(n);
    }

    /// <summary>
    /// Renders [database.]schema.base. An absent schema is NOT reliably "dbo":
    /// it resolves through the connection's default schema at execution time.
    /// Rendering the assumption keeps that dependency visible.
    /// </summary>
    static string Render(SchemaObjectName n)
    {
        var baseId = n.BaseIdentifier?.Value;
        if (string.IsNullOrEmpty(baseId)) return "";
        var schema = n.SchemaIdentifier?.Value ?? "dbo";
        var db = n.DatabaseIdentifier?.Value;
        return db is null ? $"{schema}.{baseId}" : $"{db}.{schema}.{baseId}";
    }

    // --- AST walk ------------------------------------------------------------
    //
    // ScriptDom's own visitor, NOT reflection. This is not a style preference:
    // the reflection version (GetProperties on each node) passes every test under
    // the JIT and returns ZERO nodes under NativeAOT, because the trimmer drops
    // property metadata for types nothing statically references. The guard then
    // sees no statements and classifies DROP TABLE, TRUNCATE and EXEC xp_cmdshell
    // as reads -- a fail-open gate produced purely by the build configuration.
    //
    // The visitor is dispatched by ScriptDom's own generated Accept methods, so
    // the trimmer keeps everything it needs.

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

    // --- parser selection ----------------------------------------------------

    /// <summary>
    /// Direct construction, deliberately. An earlier version picked the highest
    /// TSql*Parser by reflecting over the assembly; that works under the JIT and
    /// crashes under NativeAOT, because the trimmer removes parser types nothing
    /// statically references. Naming the type is what keeps it in the binary.
    /// </summary>
    static (TSqlParser, string) MakeParser() =>
        (new TSql180Parser(initialQuotedIdentifiers: true), nameof(TSql180Parser));

    // --- probes ---------------------------------------------------------------

    static void FailClosedProbe(TSqlParser parser)
    {
        Console.WriteLine("\n=== Fail-closed probe ===");
        string[] junk =
        [
            "@@@ not sql at all @@@",
            "SELECT * FROM",
            "SELECT * FROM orders WHERE n = 'abc",
            "SELECT 1 @@@@ DELETE FROM orders",
            "-- just a comment",
            "",
        ];
        foreach (var sql in junk)
        {
            using var reader = new StringReader(sql);
            parser.Parse(reader, out IList<ParseError> errors);
            var verdict = errors.Count > 0 ? $"REJECTED ({errors.Count} parse error(s))" : "accepted";
            Console.WriteLine($"  {Trunc(sql, 38),-40} {verdict}");
        }
    }

    static void Latency(TSqlParser parser)
    {
        Console.WriteLine("\n=== Parse latency ===");
        foreach (var sql in new[]
        {
            "SELECT * FROM orders WHERE id = 1",
            "UPDATE [dbo].[orders] SET vip = 1 WHERE id IN (SELECT id FROM vips)"
        })
        {
            Classify(parser, sql); // warm
            const int n = 500;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Classify(parser, sql);
            sw.Stop();
            Console.WriteLine($"  {Trunc(sql, 44),-46} {sw.Elapsed.TotalMicroseconds / n:F1} us/parse");
        }
    }

    static string Trunc(string s, int n)
    {
        s = s.Replace("\n", " ");
        return s.Length > n ? s[..n] + "..." : s;
    }
}
