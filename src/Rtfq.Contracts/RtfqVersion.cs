using System.Reflection;

namespace Rtfq.Contracts;

/// <summary>
/// The running build's version, read from the assembly the compiler stamped
/// rather than from a constant someone has to remember to bump.
///
/// A hand-maintained version string drifts from the tag that shipped it, and the
/// first time you notice is when a bug report cites a version that never existed.
/// </summary>
public static class RtfqVersion
{
    /// <summary>e.g. <c>0.1.0</c> for a release, <c>0.1.0-dev</c> from a working tree.</summary>
    public static string Current { get; } = Resolve();

    /// <summary>True when this build did not come from a release tag.</summary>
    public static bool IsDevelopmentBuild => Current.Contains("-dev", StringComparison.Ordinal);

    static string Resolve()
    {
        var informational = typeof(RtfqVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (string.IsNullOrEmpty(informational)) return "0.0.0-unknown";

        // The SDK appends "+<commit sha>" when source-link is in play. Keep the
        // version, drop the build metadata.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus > 0 ? informational[..plus] : informational;
    }
}
