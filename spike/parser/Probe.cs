using System.Reflection;

namespace Rtfq.Spike;

/// <summary>
/// Dumps Npgquery's public surface so the classifier is written against the
/// real API rather than a README. Loads by name so a wrong type guess here
/// cannot break the build.
/// </summary>
public static class Probe
{
    public static void Run()
    {
        var asm = Assembly.Load("Npgquery");
        Console.WriteLine($"=== {asm.GetName().Name} {asm.GetName().Version} ===");

        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
        {
            // Skip generated protobuf AST types: hundreds of them, and we only
            // need the entry points.
            var ns = t.Namespace ?? "";
            if (ns.Contains("Protobuf") || ns.Contains("Google")) continue;
            if (t.IsEnum || t.IsNested) continue;

            var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                           .Where(m => !m.IsSpecialName)
                           .OrderBy(m => m.Name)
                           .ToList();
            if (methods.Count == 0) continue;

            Console.WriteLine($"\n{t.FullName}");
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                               .OrderBy(p => p.Name))
                Console.WriteLine($"    [prop] {Short(p.PropertyType)} {p.Name}");
            foreach (var m in methods)
            {
                var ps = string.Join(", ", m.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
                Console.WriteLine($"    {(m.IsStatic ? "static " : "")}{Short(m.ReturnType)} {m.Name}({ps})");
            }
        }

        Console.WriteLine("\n=== namespaces present ===");
        foreach (var ns in asm.GetExportedTypes().Select(t => t.Namespace).Distinct().OrderBy(n => n))
            Console.WriteLine("  " + ns);
    }

    static string Short(Type t) => t.IsGenericType
        ? t.Name[..t.Name.IndexOf('`')] + "<" + string.Join(",", t.GetGenericArguments().Select(Short)) + ">"
        : t.Name;
}
