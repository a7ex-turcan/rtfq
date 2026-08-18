using System.Globalization;
using Rtfq.Contracts;

namespace Rtfq.Server.Configuration;

public sealed record RtfqConfig
{
    public required ServerSection Server { get; init; }
    public required DefaultsSection Defaults { get; init; }
    public required IReadOnlyList<SourceSection> Sources { get; init; }
    public ApprovalSection Approval { get; init; } = new();

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

    /// <summary>
    /// How long a proposal waits on a human. Longer than the ordinary handle TTL
    /// because a person is slower than a machine, and affordable precisely because
    /// an approval-required proposal holds no transaction open while it waits.
    /// </summary>
    public TimeSpan ApprovalTtl { get; init; } = TimeSpan.FromMinutes(10);
}

/// <summary>
/// How a human is asked. The default queues in this process and is answered with
/// <c>rtfq approvals</c>; <c>webhook</c> hands the question to something else
/// entirely, which is how Slack gets built without Slack living in core.
/// </summary>
public sealed record ApprovalSection
{
    /// <summary>local or webhook.</summary>
    public string Mode { get; init; } = "local";

    /// <summary>Base address of the approval service. Required when <see cref="Mode"/> is webhook.</summary>
    public string Endpoint { get; init; } = "";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Sent on every call — an API key for the approval service, typically.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether any header value was written into the file rather than referenced.</summary>
    public bool HeadersHadInlineSecret { get; init; }
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

    /// <summary>MongoDB: databases to introspect. Empty means every non-system database.</summary>
    public IReadOnlyList<string> Databases { get; init; } = [];

    // --- the write path -----------------------------------------------------

    /// <summary>
    /// Targets this source will accept mutations against. **Empty means nothing
    /// is writable**, whatever <see cref="Access"/> says — an absent allow-list is
    /// absent, not permissive.
    ///
    /// Entries are schema-qualified and may use <c>*</c>, so <c>dbo.*</c> covers a
    /// whole schema (ADR 0008). Matching is ordinal and case-sensitive:
    /// <c>"Orders"</c> and <c>orders</c> are different tables.
    /// <see cref="DenyTables"/> still wins.
    /// </summary>
    public IReadOnlyList<string> WritableTables { get; init; } = [];

    /// <summary>
    /// Targets refused outright, for reads as well as writes. Supports a trailing
    /// <c>*</c>. Deny beats allow, always.
    /// </summary>
    public IReadOnlyList<string> DenyTables { get; init; } = [];

    /// <summary>
    /// Blocks the commit until a human approves. The proposal runs, its diff is
    /// captured, and it rolls straight back; commit re-runs it and refuses unless
    /// the diff still matches what was approved.
    /// </summary>
    public bool RequireApproval { get; init; }

    /// <summary>Per-source override of the affected-row cap.</summary>
    public int? MaxAffectedRows { get; init; }

    /// <summary>
    /// Writing requires a deliberate <c>rtfq unlock</c> first, even where access
    /// and grants already permit it. Off by default; turning it on is how a
    /// source says yes in principle, but not right now.
    /// </summary>
    public bool RequireUnlock { get; init; }

    public int EffectiveMaxAffectedRows(DefaultsSection d) => MaxAffectedRows ?? d.MaxAffectedRows;

    // --- HTTP sources -------------------------------------------------------

    public string BaseUrl { get; init; } = "";

    /// <summary>Permitted methods. Absent means GET only — never inferred as "all".</summary>
    public IReadOnlyList<string> Methods { get; init; } = [];

    /// <summary>
    /// Permitted paths, with a trailing <c>*</c> allowed as a prefix wildcard.
    /// Empty means nothing is reachable: an HTTP source with no allow-list is not
    /// an open one.
    /// </summary>
    public IReadOnlyList<string> AllowPaths { get; init; } = [];

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();

    /// <summary>Whether any header value was written into the file rather than referenced.</summary>
    public bool HeadersHadInlineSecret { get; init; }

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
