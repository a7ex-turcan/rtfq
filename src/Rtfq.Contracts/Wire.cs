using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Rtfq.Contracts;

// The stable HTTP+JSON contract. Both the CLI and (from M1) the MCP adapter are
// clients of this; nothing here may depend on ASP.NET Core or on a driver.
//
// Wire names are snake_case, applied by the serializer's naming policy rather
// than by attributes, so the C# stays idiomatic and the JSON stays conventional.

/// <param name="Detail">
/// Extra context for a human. Never a natural-language summary an agent supplied:
/// per CLAUDE.md principle 3, tool output must not influence what we report.
/// </param>
public sealed record ErrorBody(string Code, string Message, string? Detail = null);

public sealed record ErrorResponse(ErrorBody Error);

public sealed record QueryRequest
{
    public required string Source { get; init; }
    public required string Statement { get; init; }

    /// <summary>
    /// Caller's row ceiling. Clamped down to the source's configured
    /// <c>max_rows</c> and never up: a caller cannot raise its own cap.
    /// </summary>
    public int? MaxRows { get; init; }
}

public sealed record ColumnInfo(string Name, string Type);

/// <summary>
/// Every read response carries the full envelope. <see cref="Truncated"/> is not
/// optional decoration: silent truncation is a bug, so a caller must always be
/// able to tell a complete result from a clipped one.
/// </summary>
public sealed record QueryResponse
{
    public required List<ColumnInfo> Columns { get; init; }

    /// <summary>
    /// Rows as a JSON array of arrays, positionally matching <see cref="Columns"/>.
    /// Modelled as <see cref="JsonArray"/> rather than <c>object?[]</c> so cell
    /// types survive the wire without reflection-based polymorphic serialization,
    /// which NativeAOT cannot do (ADR 0001).
    /// </summary>
    public required JsonArray Rows { get; init; }

    public required int RowCount { get; init; }
    public required bool Truncated { get; init; }
    public required long ElapsedMs { get; init; }

    /// <summary>
    /// Always null. Kept in the envelope because it shipped in 0.1.0 and because
    /// the shape is right if a dialect ever offers a safe cursor — but per
    /// ADR 0003 truncation is terminal and there is no pagination.
    /// </summary>
    public string? NextCursor { get; init; }

    /// <summary>
    /// Present when <see cref="Truncated"/> is true: what the caller should do
    /// instead. A truncated response that only reports the fact leaves an agent
    /// to guess, and the guess is usually "ask again", which cannot work.
    /// </summary>
    public string? Hint { get; init; }
}

/// <summary>How old the schema being described is. Present on every discovery response.</summary>
public sealed record SchemaFreshness
{
    public required string CapturedAt { get; init; }
    public required long AgeSeconds { get; init; }
    public required bool Stale { get; init; }

    /// <summary>True when this was served from cache because the source could not be reached.</summary>
    public bool Offline { get; init; }
}

public sealed record TableSummary
{
    /// <summary>Schema-qualified.</summary>
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public long? EstimatedRows { get; init; }
    public required int Columns { get; init; }
}

public sealed record DescribeSourceResponse
{
    public required string Source { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }
    public required string EffectiveAccess { get; init; }
    public required SchemaFreshness Schema { get; init; }

    /// <summary>Total tables in the source, which may exceed <see cref="Tables"/> when the list is clipped.</summary>
    public required int TableCount { get; init; }
    public required List<TableSummary> Tables { get; init; }

    public required bool Truncated { get; init; }
    public string? Hint { get; init; }
}

public sealed record ColumnDetail(string Name, string Type, bool Nullable, string? Default);

public sealed record IndexDetail(string Name, List<string> Columns, bool Unique, bool Primary);

public sealed record ForeignKeyDetail(List<string> Columns, string References, List<string> ReferencedColumns);

public sealed record DescribeTableResponse
{
    public required string Table { get; init; }
    public required string Kind { get; init; }
    public long? EstimatedRows { get; init; }

    /// <summary>
    /// Whether this caller could write here: the source declares it, the token
    /// was granted it, and the table is on the write allow-list. Says nothing
    /// about a time-boxed unlock, which is a fact about right now.
    /// </summary>
    public required bool Writable { get; init; }

    public required SchemaFreshness Schema { get; init; }
    public required List<ColumnDetail> Columns { get; init; }
    public List<string> PrimaryKey { get; init; } = [];
    public List<IndexDetail> Indexes { get; init; } = [];
    public List<ForeignKeyDetail> ForeignKeys { get; init; } = [];
}

public sealed record ExplainRequest
{
    public required string Source { get; init; }
    public required string Statement { get; init; }
}

public sealed record ExplainResponse
{
    public required string Plan { get; init; }
    public required long ElapsedMs { get; init; }
}

public sealed record SampleRequest
{
    public required string Source { get; init; }
    public required string Table { get; init; }
    public int? Rows { get; init; }
}

public sealed record ProposeWriteRequest
{
    public required string Source { get; init; }
    public required string Statement { get; init; }
}

/// <summary>
/// A mutation that has run inside a transaction and is <b>not committed</b>.
///
/// The agent is structurally forced to look before it leaps: it cannot reach a
/// committed change without first receiving this and deciding on it.
/// </summary>
public sealed record ProposeWriteResponse
{
    public required string Handle { get; init; }
    public required string Source { get; init; }

    /// <summary>mutation or schema.</summary>
    public required string Kind { get; init; }
    public required string Target { get; init; }

    /// <summary>The driver's real count from the uncommitted execution. Null for a schema change.</summary>
    public int? AffectedRows { get; init; }

    /// <summary>Columns of <see cref="DiffSample"/>.</summary>
    public required List<ColumnInfo> DiffColumns { get; init; }

    /// <summary>The affected rows as they were <i>before</i> the statement ran.</summary>
    public required JsonArray DiffSample { get; init; }

    public required bool RequiresApproval { get; init; }
    public required string ExpiresAt { get; init; }

    /// <summary>Hash of the statement this handle came from, so a caller can confirm what it is committing.</summary>
    public required string Fingerprint { get; init; }

    public string? SchemaSummary { get; init; }
    public string? Hint { get; init; }
}

public sealed record SettleWriteRequest
{
    public required string Handle { get; init; }
}

public sealed record SettleWriteResponse
{
    public required string Handle { get; init; }

    /// <summary>committed, aborted, or pending while a human has not yet decided.</summary>
    public required string Outcome { get; init; }

    public int? AffectedRows { get; init; }

    /// <summary>Who approved it, when a human did.</summary>
    public string? Approver { get; init; }

    public string? Hint { get; init; }
}

public sealed record SourceInfo
{
    public required string Name { get; init; }
    public required string Kind { get; init; }
    public required string Description { get; init; }

    /// <summary>What the source itself permits, before the caller is considered.</summary>
    public required string Access { get; init; }

    /// <summary>
    /// The intersection of the source's access and this caller's grant: what this
    /// caller may actually do. The only field a client should reason about.
    /// </summary>
    public required string EffectiveAccess { get; init; }

    public required List<string> Capabilities { get; init; }
}

public sealed record SourcesResponse(List<SourceInfo> Sources);

public sealed record HealthResponse(string Status, string Version);

[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(QueryRequest))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(SourcesResponse))]
[JsonSerializable(typeof(HealthResponse))]
[JsonSerializable(typeof(DescribeSourceResponse))]
[JsonSerializable(typeof(DescribeTableResponse))]
[JsonSerializable(typeof(ExplainRequest))]
[JsonSerializable(typeof(ExplainResponse))]
[JsonSerializable(typeof(SampleRequest))]
[JsonSerializable(typeof(ProposeWriteRequest))]
[JsonSerializable(typeof(ProposeWriteResponse))]
[JsonSerializable(typeof(SettleWriteRequest))]
[JsonSerializable(typeof(SettleWriteResponse))]
[JsonSerializable(typeof(JsonArray))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class RtfqJson : JsonSerializerContext;
