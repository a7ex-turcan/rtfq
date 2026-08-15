using System.Globalization;
using Rtfq.Contracts;

namespace Rtfq.Server.Configuration;

public sealed record RtfqConfig
{
    public required ServerSection Server { get; init; }
    public required DefaultsSection Defaults { get; init; }
    public required IReadOnlyList<SourceSection> Sources { get; init; }

    public SourceSection? FindSource(string name) =>
        Sources.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
}

public sealed record ServerSection
{
    public required string Listen { get; init; }
    public TlsSection? Tls { get; init; }
    public required AuthSection Auth { get; init; }
}

public sealed record TlsSection
{
    public required string CertPath { get; init; }
    public required string KeyPath { get; init; }
}

public sealed record AuthSection
{
    public required string Mode { get; init; }
    public required IReadOnlyList<TokenSection> Tokens { get; init; }
}

public sealed record TokenSection
{
    public required string Id { get; init; }
    public required string Secret { get; init; }

    /// <summary>Whether <see cref="Secret"/> came from a reference rather than being inline in the file.</summary>
    public required bool SecretWasReference { get; init; }

    public required IReadOnlyDictionary<string, AccessLevel> Grants { get; init; }
}

public sealed record DefaultsSection
{
    public int MaxRows { get; init; } = 1000;
    public int MaxAffectedRows { get; init; } = 50;
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(3);
    public TimeSpan WriteHandleTtl { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long a schema snapshot stays fresh. Past this it is still served —
    /// flagged with its age — while a refresh runs behind the response (ADR 0003).
    /// </summary>
    public TimeSpan SchemaCacheTtl { get; init; } = TimeSpan.FromMinutes(15);
}

public sealed record SourceSection
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Dsn { get; init; }
    public required bool DsnWasReference { get; init; }
    public string Description { get; init; } = "";
    public AccessLevel Access { get; init; } = AccessLevel.Read;
    public IReadOnlyList<string> Schemas { get; init; } = [];

    public int? MaxRows { get; init; }
    public TimeSpan? StatementTimeout { get; init; }

    public int EffectiveMaxRows(DefaultsSection d) => MaxRows ?? d.MaxRows;
    public TimeSpan EffectiveStatementTimeout(DefaultsSection d) => StatementTimeout ?? d.StatementTimeout;
}

/// <summary>
/// Durations are written the way operators write them ("15s", "2m"), not as ISO-8601.
/// </summary>
public static class Duration
{
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var s = text.Trim();
        var unit = s[^1];
        var digits = s[..^1];

        if (!char.IsLetter(unit) || !double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
            return false;
        if (n < 0) return false;

        value = unit switch
        {
            's' => TimeSpan.FromSeconds(n),
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            _ => TimeSpan.Zero,
        };
        return value != TimeSpan.Zero || n == 0;
    }
}
