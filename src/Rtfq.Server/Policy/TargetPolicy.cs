using Rtfq.Server.Configuration;

namespace Rtfq.Server.Policy;

public enum TargetOutcome
{
    Allowed,

    /// <summary>Matched a deny rule. Deny beats allow, always.</summary>
    Denied,

    /// <summary>Not on the write allow-list — which includes the case of there being no allow-list.</summary>
    NotWritable,
}

/// <summary>
/// Gate three of four: is this specific table one this source will accept a
/// mutation against?
///
/// Two rules, and their asymmetry is the point. **Deny is a glob** so a single
/// entry can cover a family (<c>*.pii_*</c>), because the cost of a deny rule
/// matching too much is an inconvenience. **Allow is exact**, because the cost of
/// an allow rule matching too much is a table nobody meant to expose.
///
/// Comparison is ordinal and case-sensitive throughout: <c>"Orders"</c> and
/// <c>orders</c> are genuinely different tables in PostgreSQL, and folding them
/// together would be a gate bypass (ADR 0001).
/// </summary>
public static class TargetPolicy
{
    /// <summary>Whether a mutation may touch <paramref name="target"/>.</summary>
    public static TargetOutcome EvaluateWrite(SourceSection source, string target)
    {
        if (IsDenied(source, target)) return TargetOutcome.Denied;

        // An absent allow-list is absent, not permissive.
        if (source.WritableTables.Count == 0) return TargetOutcome.NotWritable;

        return source.WritableTables.Contains(target, StringComparer.Ordinal)
            ? TargetOutcome.Allowed
            : TargetOutcome.NotWritable;
    }

    /// <summary>
    /// Whether a statement may touch these objects at all. Applies to reads too:
    /// a denied table is denied however it is reached, including through a join
    /// the caller hoped nobody would look at.
    /// </summary>
    public static string? FirstDenied(SourceSection source, IEnumerable<string> targets) =>
        targets.FirstOrDefault(t => IsDenied(source, t));

    static bool IsDenied(SourceSection source, string target) =>
        source.DenyTables.Any(pattern => GlobMatches(pattern, target));

    /// <summary>
    /// Glob match supporting <c>*</c> anywhere, so <c>*.pii_*</c> works as
    /// CLAUDE.md's example config uses it. Implemented directly rather than by
    /// translating to a regular expression, because a config value turning into a
    /// pattern language is how a denial rule ends up meaning something other than
    /// it reads.
    /// </summary>
    internal static bool GlobMatches(string pattern, string value)
    {
        var p = 0;
        var v = 0;
        var star = -1;
        var mark = 0;

        while (v < value.Length)
        {
            if (p < pattern.Length && (pattern[p] == value[v]))
            {
                p++;
                v++;
            }
            else if (p < pattern.Length && pattern[p] == '*')
            {
                star = p++;
                mark = v;
            }
            else if (star >= 0)
            {
                p = star + 1;
                v = ++mark;
            }
            else
            {
                return false;
            }
        }

        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
