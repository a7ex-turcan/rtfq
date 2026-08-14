namespace Rtfq.Server.Configuration;

public enum Severity { Warning, Error }

/// <param name="Check">
/// The named check that produced this. Named rather than anonymous so a
/// diagnostic can be cited in docs and matched in tests.
/// </param>
/// <param name="Path">Dotted path into the config, e.g. <c>sources[0].dsn</c>.</param>
/// <param name="Line">1-based line in the config file, or 0 if not known.</param>
public sealed record Diagnostic(string Check, Severity Severity, string Message, string Path = "", int Line = 0)
{
    public override string ToString()
    {
        var where = Line > 0 ? $"line {Line}" : Path;
        if (Line > 0 && Path.Length > 0) where = $"line {Line}, {Path}";
        var prefix = Severity == Severity.Error ? "error" : "warning";
        return where.Length > 0
            ? $"{prefix} [{Check}] {where}: {Message}"
            : $"{prefix} [{Check}]: {Message}";
    }
}

public sealed record ValidationResult(IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
    public IEnumerable<Diagnostic> Errors => Diagnostics.Where(d => d.Severity == Severity.Error);
    public IEnumerable<Diagnostic> Warnings => Diagnostics.Where(d => d.Severity == Severity.Warning);
}
