using System.Text;

namespace Rtfq.Server.Configuration;

/// <summary>
/// Resolves <c>${env:NAME}</c> and <c>${file:/path}</c> references.
///
/// Per CLAUDE.md principle 5 we reference secrets and never store them, so this
/// also reports whether a value <i>was</i> a reference — which is what lets the
/// validator warn in dev and hard-fail in production on an inline password.
/// </summary>
public static class SecretResolver
{
    public readonly record struct Resolution(string Value, bool WasReference, string? Error);

    public static Resolution Resolve(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return new("", false, null);
        if (!raw.Contains("${", StringComparison.Ordinal)) return new(raw, false, null);

        var sb = new StringBuilder(raw.Length);
        var sawReference = false;
        var i = 0;

        while (i < raw.Length)
        {
            var open = raw.IndexOf("${", i, StringComparison.Ordinal);
            if (open < 0) { sb.Append(raw, i, raw.Length - i); break; }

            var close = raw.IndexOf('}', open);
            if (close < 0) return new(raw, false, "unterminated '${' reference");

            sb.Append(raw, i, open - i);

            var body = raw[(open + 2)..close];
            var colon = body.IndexOf(':');
            if (colon <= 0) return new(raw, false, $"malformed reference '${{{body}}}' - expected ${{env:NAME}} or ${{file:/path}}");

            var scheme = body[..colon].Trim();
            var target = body[(colon + 1)..].Trim();
            sawReference = true;

            switch (scheme)
            {
                case "env":
                {
                    var value = Environment.GetEnvironmentVariable(target);
                    if (value is null) return new(raw, true, $"environment variable '{target}' is not set");
                    sb.Append(value);
                    break;
                }
                case "file":
                {
                    if (!File.Exists(target)) return new(raw, true, $"secret file '{target}' does not exist");
                    try { sb.Append(File.ReadAllText(target).Trim()); }
                    catch (IOException ex) { return new(raw, true, $"secret file '{target}' is unreadable: {ex.Message}"); }
                    break;
                }
                case "vault":
                    // Deliberately unimplemented rather than silently ignored: an
                    // operator who writes ${vault:...} must not get an empty secret.
                    return new(raw, true, "${vault:...} is not supported yet - use ${env:} or ${file:}");
                default:
                    return new(raw, false, $"unknown reference scheme '{scheme}' - expected env, file or vault");
            }

            i = close + 1;
        }

        return new(sb.ToString(), sawReference, null);
    }

    /// <summary>
    /// Whether a connection string carries an inline password. A DSN with no
    /// password at all is fine (peer/IAM auth); one with the password written into
    /// the file is the case we refuse in production.
    /// </summary>
    public static bool LooksLikeInlineSecret(string dsn)
    {
        if (string.IsNullOrEmpty(dsn)) return false;

        // Key/value form: "Host=...;Password=hunter2"
        if (dsn.Contains("password=", StringComparison.OrdinalIgnoreCase) ||
            dsn.Contains("pwd=", StringComparison.OrdinalIgnoreCase))
            return true;

        // URI form: "postgres://user:hunter2@host/db"
        var schemeEnd = dsn.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) return false;

        var authorityEnd = dsn.IndexOf('/', schemeEnd + 3);
        var authority = authorityEnd < 0 ? dsn[(schemeEnd + 3)..] : dsn[(schemeEnd + 3)..authorityEnd];
        var at = authority.LastIndexOf('@');
        return at > 0 && authority[..at].Contains(':');
    }
}
