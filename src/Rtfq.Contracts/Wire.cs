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

    /// <summary>Reserved for M1 cursor pagination; always null in M0.</summary>
    public string? NextCursor { get; init; }
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
[JsonSerializable(typeof(JsonArray))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class RtfqJson : JsonSerializerContext;
