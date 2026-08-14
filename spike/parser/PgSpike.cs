using System.Diagnostics;
using System.Text.Json;
using Npgquery;

namespace Rtfq.Spike;

/// <summary>
/// Runs the PostgreSQL corpus through Npgquery, a P/Invoke wrapper over
/// libpg_query - PostgreSQL's own parser as a native library.
///
/// The parse tree is libpg_query's JSON, byte-identical to what the Go spike
/// walked, so the guard logic ports directly and the two runtimes are judged on
/// the same evidence.
/// </summary>
public static class PgSpike
{
    // Allow-list, not a DDL deny-list: COPY ... FROM PROGRAM, DO, GRANT and SET
    // are none of them DDL and all of them are catastrophic.
    static readonly HashSet<string> Allowed =
        ["SelectStmt", "InsertStmt", "UpdateStmt", "DeleteStmt", "MergeStmt", "ExplainStmt"];

    static readonly HashSet<string> Mutating =
        ["InsertStmt", "UpdateStmt", "DeleteStmt", "MergeStmt"];

    record Outcome(Verdict Verdict, string Target, string Detail);

    public static int Run()
    {
        Console.WriteLine("=== PostgreSQL: Npgquery (libpg_query via P/Invoke) ===");
        Console.WriteLine();

        // First call pays native library resolution + load.
        var cold = Stopwatch.StartNew();
        _ = Parser.QuickParse("SELECT 1", new ParseOptions());
        cold.Stop();

        int pass = 0, fail = 0;
        var failures = new List<string>();

        foreach (var c in Corpus.Pg)
        {
            var got = Classify(c.Sql);
            bool ok = got.Verdict == c.Want;
            bool targetOk = !ok || c.Target.Length == 0 || got.Target == c.Target;

            if (ok && targetOk) pass++;
            else
            {
                fail++;
                failures.Add($"{c.Name,-22} want={c.Want,-8} got={got.Verdict,-8} target want=\"{c.Target}\" got=\"{got.Target}\" ({got.Detail})");
            }
            Console.WriteLine($"{(ok && targetOk ? "PASS" : "FAIL")}  {c.Name,-22} {got.Verdict,-8} {got.Target,-26} {got.Detail}");
        }

        Console.WriteLine();
        Console.WriteLine($"RESULT: {pass} passed, {fail} failed, {Corpus.Pg.Length} total");
        if (failures.Count > 0)
        {
            Console.WriteLine("\nFAILURES:");
            foreach (var f in failures) Console.WriteLine("  " + f);
        }

        Console.WriteLine($"\n=== Native library cold start ===");
        Console.WriteLine($"  first parse (incl. native load): {cold.Elapsed.TotalMilliseconds:F1} ms");

        FailClosed();
        Functions();
        DeparseRoundTrip();
        Latency();

        return fail > 0 ? 1 : 0;
    }

    // --- classification -----------------------------------------------------

    static Outcome Classify(string sql)
    {
        ParseResult result;
        try { result = Parser.QuickParse(sql, new ParseOptions()); }
        catch (Exception ex) { return new(Verdict.Reject, "", $"threw {ex.GetType().Name}"); }

        if (result.IsError)
            return new(Verdict.Reject, "", "parse error: " + FirstLine(result.Error ?? ""));

        var root = result.ParseTree.RootElement;
        if (!root.TryGetProperty("stmts", out var stmts) || stmts.ValueKind != JsonValueKind.Array)
            return new(Verdict.Reject, "", "no statements");

        int count = stmts.GetArrayLength();
        if (count == 0) return new(Verdict.Reject, "", "no statement");
        if (count > 1) return new(Verdict.Reject, "", $"multi-statement ({count})");

        // Exhaustive walk: a write buried in a CTE cannot hide behind a SELECT.
        var found = new HashSet<string>();
        var stmtNodes = new List<(string Name, JsonElement Body)>();
        foreach (var (key, value) in Walk(root))
        {
            if (IsStmtKey(key))
            {
                found.Add(key);
                stmtNodes.Add((key, value));
            }
        }

        foreach (var name in found)
            if (!Allowed.Contains(name))
                return new(Verdict.Reject, "", "disallowed node: " + name);

        if (found.Contains("ExplainStmt") && HasAnalyze(root))
            return new(Verdict.Reject, "", "EXPLAIN ANALYZE executes");

        if (HasKeyAnywhere(root, "intoClause"))
            return new(Verdict.Reject, "", "SELECT INTO creates a relation");

        bool isMutation = found.Any(Mutating.Contains);
        if (!isMutation)
            return new(Verdict.Read, FirstRelation(root), "read");

        foreach (var (name, body) in stmtNodes)
        {
            if (name is not ("UpdateStmt" or "DeleteStmt")) continue;
            if (!body.TryGetProperty("whereClause", out var where))
                return new(Verdict.Reject, "", "unqualified " + name);
            if (TriviallyTrue(where))
                return new(Verdict.Reject, "", "trivially-true predicate");
        }

        return new(Verdict.Mutation, WriteTarget(stmtNodes), "bounded mutation");
    }

    // --- tree helpers --------------------------------------------------------

    /// <summary>
    /// Node type keys are upper-camel ("SelectStmt"); field names are lower-camel
    /// ("stmt", "whereClause"), so the leading capital is the discriminator.
    /// </summary>
    static bool IsStmtKey(string key) => key.EndsWith("Stmt") && char.IsUpper(key[0]);

    static IEnumerable<(string Key, JsonElement Value)> Walk(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in e.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                        yield return (prop.Name, prop.Value);
                    foreach (var inner in Walk(prop.Value)) yield return inner;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray())
                    foreach (var inner in Walk(item)) yield return inner;
                break;
        }
    }

    static bool HasKeyAnywhere(JsonElement root, string key) =>
        Walk(root).Any(x => x.Key == key) ||
        (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(key, out _));

    static bool HasAnalyze(JsonElement root) =>
        Walk(root).Any(x => x.Key == "DefElem"
            && x.Value.TryGetProperty("defname", out var n)
            && string.Equals(n.GetString(), "analyze", StringComparison.OrdinalIgnoreCase));

    /// <summary>WHERE true, WHERE 1=1, and any OR-branch reducing to either.</summary>
    static bool TriviallyTrue(JsonElement node)
    {
        if (node.ValueKind != JsonValueKind.Object) return false;

        if (node.TryGetProperty("A_Const", out var konst)
            && konst.TryGetProperty("boolval", out var bv)
            && bv.TryGetProperty("boolval", out var b)
            && b.ValueKind == JsonValueKind.True)
            return true;

        if (node.TryGetProperty("A_Expr", out var expr)
            && OpName(expr) == "="
            && SameConst(expr, "lexpr", "rexpr"))
            return true;

        if (node.TryGetProperty("BoolExpr", out var boolExpr))
        {
            var op = boolExpr.TryGetProperty("boolop", out var o) ? o.GetString() : null;
            if (!boolExpr.TryGetProperty("args", out var args) || args.ValueKind != JsonValueKind.Array)
                return false;
            var items = args.EnumerateArray().ToList();
            return op switch
            {
                "OR_EXPR" => items.Any(TriviallyTrue),
                "AND_EXPR" => items.Count > 0 && items.All(TriviallyTrue),
                _ => false
            };
        }
        return false;
    }

    static string OpName(JsonElement expr)
    {
        if (!expr.TryGetProperty("name", out var names) || names.ValueKind != JsonValueKind.Array)
            return "";
        foreach (var n in names.EnumerateArray())
            if (n.TryGetProperty("String", out var s) && s.TryGetProperty("sval", out var v))
                return v.GetString() ?? "";
        return "";
    }

    static bool SameConst(JsonElement expr, string leftKey, string rightKey)
    {
        if (!expr.TryGetProperty(leftKey, out var l) || !expr.TryGetProperty(rightKey, out var r))
            return false;
        var ls = StripLocation(l);
        var rs = StripLocation(r);
        return ls.Contains("A_Const") && ls == rs;
    }

    /// <summary>Normalizes away byte offsets so two identical literals compare equal.</summary>
    static string StripLocation(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var parts = e.EnumerateObject()
                             .Where(p => p.Name != "location")
                             .OrderBy(p => p.Name, StringComparer.Ordinal)
                             .Select(p => $"\"{p.Name}\":{StripLocation(p.Value)}");
                return "{" + string.Join(",", parts) + "}";
            case JsonValueKind.Array:
                return "[" + string.Join(",", e.EnumerateArray().Select(StripLocation)) + "]";
            default:
                return e.GetRawText();
        }
    }

    static string WriteTarget(List<(string Name, JsonElement Body)> nodes)
    {
        foreach (var (name, body) in nodes)
        {
            if (!Mutating.Contains(name)) continue;
            if (body.TryGetProperty("relation", out var rel))
                return RangeVarName(rel);
        }
        return "";
    }

    static string FirstRelation(JsonElement root)
    {
        foreach (var (key, value) in Walk(root))
            if (key == "RangeVar") return RangeVarName(value);
        return "";
    }

    /// <summary>
    /// Renders schema.relation. An absent schemaname is NOT "public" in general:
    /// it resolves through search_path at execution time. Rendering the assumption
    /// keeps that dependency visible.
    /// </summary>
    static string RangeVarName(JsonElement rv)
    {
        string? Get(string k) => rv.TryGetProperty(k, out var v) ? v.GetString() : null;
        var rel = Get("relname") ?? "";
        var schema = Get("schemaname") ?? "public";
        var catalog = Get("catalogname");
        return catalog is null ? $"{schema}.{rel}" : $"{catalog}.{schema}.{rel}";
    }

    // --- probes ---------------------------------------------------------------

    static void FailClosed()
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
            var r = Parser.QuickParse(sql, new ParseOptions());
            var state = r.IsError ? $"REJECTED ({FirstLine(r.Error ?? "")})" : "accepted";
            Console.WriteLine($"  {Trunc(sql, 38),-40} {state}");
        }
    }

    static void Functions()
    {
        Console.WriteLine("\n=== Function calls inside a statement that classifies as a read ===");
        foreach (var sql in new[]
        {
            "SELECT dblink_exec('host=evil', 'DELETE FROM orders')",
            "SELECT lo_export(1, '/tmp/x')",
            "SELECT pg_read_file('/etc/passwd')",
            "SELECT count(*) FROM orders WHERE created_at > now()",
        })
        {
            var r = Parser.QuickParse(sql, new ParseOptions());
            var funcs = new SortedSet<string>();
            if (!r.IsError)
                foreach (var (key, value) in Walk(r.ParseTree.RootElement))
                    if (key == "FuncCall" && value.TryGetProperty("funcname", out var fn))
                        funcs.Add(DottedName(fn));
            Console.WriteLine($"  {Trunc(sql, 52),-54} funcs=[{string.Join(", ", funcs)}]");
        }
    }

    static string DottedName(JsonElement names)
    {
        var parts = new List<string>();
        foreach (var n in names.EnumerateArray())
            if (n.TryGetProperty("String", out var s) && s.TryGetProperty("sval", out var v))
                parts.Add(v.GetString() ?? "");
        return string.Join(".", parts);
    }

    static void DeparseRoundTrip()
    {
        Console.WriteLine("\n=== Deparse round-trip (LIMIT injection depends on this) ===");
        var r = Parser.QuickParse("SELECT id, name FROM public.orders WHERE id > 5", new ParseOptions());
        if (r.IsError) { Console.WriteLine("  parse failed: " + r.Error); return; }
        try
        {
            var d = Parser.QuickDeparse(r.ParseTree);
            foreach (var p in d.GetType().GetProperties())
            {
                object? v = null;
                try { v = p.GetValue(d); } catch { }
                if (v is string s && s.Length > 0) Console.WriteLine($"  {p.Name} = {s}");
                if (v is bool bo) Console.WriteLine($"  {p.Name} = {bo}");
            }
        }
        catch (Exception ex) { Console.WriteLine("  deparse threw: " + ex.Message); }
    }

    static void Latency()
    {
        Console.WriteLine("\n=== Parse latency ===");
        foreach (var (label, sql) in new[]
        {
            ("simple select", "SELECT * FROM orders WHERE id = 1"),
            ("complex", "WITH a AS (SELECT 1), b AS (UPDATE orders SET vip = true WHERE id = 1 RETURNING id) SELECT * FROM b JOIN a ON true"),
        })
        {
            Classify(sql);
            const int n = 500;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++) Classify(sql);
            sw.Stop();
            Console.WriteLine($"  {label,-14} {sw.Elapsed.TotalMicroseconds / n:F1} us/parse (classify incl. JSON walk)");
        }
    }

    static string FirstLine(string s)
    {
        var i = s.IndexOf('\n');
        return i >= 0 ? s[..i] : s;
    }

    static string Trunc(string s, int n)
    {
        s = s.Replace("\n", " ");
        return s.Length > n ? s[..n] + "..." : s;
    }
}
