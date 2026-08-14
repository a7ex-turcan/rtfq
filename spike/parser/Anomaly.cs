using System.Text.Json;
using Npgquery;

namespace Rtfq.Spike;

/// <summary>
/// libpg_query accepted "SELECT 1 @@@@ DELETE FROM orders" without error, which
/// is the exact shape a fail-open parser would show. This checks whether it
/// dropped the tail (a guard bypass) or genuinely parsed the whole string.
///
/// '@' is a valid PostgreSQL operator character, so a custom operator named
/// @@@@ is legal syntax - the question is what the tree then contains.
/// </summary>
public static class Anomaly
{
    public static int Run()
    {
        string[] cases =
        [
            "SELECT 1 @@@@ DELETE FROM orders",
            "SELECT 1 @@@@ 2",
            "SELECT 1 ### DROP TABLE orders",
            "@@@ DROP TABLE orders",
        ];

        foreach (var sql in cases)
        {
            Console.WriteLine($"### {sql}");
            var r = Parser.QuickParse(sql, new ParseOptions());
            if (r.IsError)
            {
                Console.WriteLine($"    REJECTED: {r.Error}");
                Console.WriteLine();
                continue;
            }

            var root = r.ParseTree.RootElement;
            int n = root.TryGetProperty("stmts", out var stmts) && stmts.ValueKind == JsonValueKind.Array
                ? stmts.GetArrayLength() : 0;

            var types = new SortedSet<string>();
            Collect(root, types);

            Console.WriteLine($"    accepted: statements={n}  stmtTypes=[{string.Join(", ", types)}]");
            var raw = root.GetRawText();
            Console.WriteLine($"    tree: {(raw.Length > 420 ? raw[..420] + " ...[truncated]" : raw)}");
            Console.WriteLine();
        }
        return 0;
    }

    static void Collect(JsonElement e, SortedSet<string> into)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.Name.EndsWith("Stmt") && char.IsUpper(p.Name[0])) into.Add(p.Name);
                    Collect(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) Collect(item, into);
                break;
        }
    }
}
