namespace Rtfq.Server;

/// <summary>
/// Where RTFQ keeps the things it must remember: the audit log now, the schema
/// cache in M1, write handles in M3. Never a database of our own — files, written
/// atomically.
/// </summary>
public static class StateDirectory
{
    public const string EnvVar = "RTFQ_STATE_DIR";

    /// <summary>
    /// Resolution order: explicit flag, then <c>RTFQ_STATE_DIR</c>, then the
    /// platform convention (<c>XDG_STATE_HOME</c> on Unix, LocalApplicationData
    /// on Windows).
    /// </summary>
    public static string Resolve(string? explicitPath = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath);

        var fromEnv = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(fromEnv))
            return Path.GetFullPath(fromEnv);

        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rtfq");

        var xdg = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return Path.Combine(xdg, "rtfq");

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "state", "rtfq");
    }

    public static string EnsureCreated(string? explicitPath = null)
    {
        var dir = Resolve(explicitPath);
        Directory.CreateDirectory(dir);
        return dir;
    }
}
