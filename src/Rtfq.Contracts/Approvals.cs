using System.Text.Json.Serialization;

namespace Rtfq.Contracts;

/// <summary>
/// One change waiting on a human.
///
/// Carries the statement and the data, and deliberately carries no explanation.
/// Per CLAUDE.md principle 3 the approval prompt shows the statement and the
/// diff, never a natural-language summary the agent supplied — because the case
/// this gate exists for is an agent that has been persuaded by a poisoned row,
/// and such an agent writes a very reassuring summary.
/// </summary>
public sealed record PendingApprovalInfo
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string TokenId { get; init; }
    public required string Target { get; init; }
    public required string Kind { get; init; }
    public required string Statement { get; init; }
    public int? AffectedRows { get; init; }
    public required List<string> DiffColumns { get; init; }

    /// <summary>The affected rows as they are now, as a JSON array of arrays.</summary>
    public required string DiffRows { get; init; }

    public required string Fingerprint { get; init; }
    public required string RequestedAt { get; init; }
    public required string ExpiresAt { get; init; }
}

public sealed record PendingApprovalsResponse(List<PendingApprovalInfo> Pending);

public sealed record ApprovalDecisionRequest
{
    public required string Id { get; init; }
    public required bool Approved { get; init; }

    /// <summary>Who is deciding. Recorded in the audit log beside the statement.</summary>
    public required string Approver { get; init; }

    public string? Reason { get; init; }
}

public sealed record ApprovalDecisionResponse(string Id, string Outcome);

public sealed record UnlockRequest
{
    public required string Source { get; init; }

    /// <summary>write or schema.</summary>
    public required string Level { get; init; }

    /// <summary>How long the window lasts, in the usual duration form: 15m, 1h.</summary>
    public string? Ttl { get; init; }
}

public sealed record UnlockInfo
{
    public required string Source { get; init; }
    public required string Level { get; init; }
    public required string Who { get; init; }
    public required string ExpiresAt { get; init; }
}

public sealed record UnlockResponse(List<UnlockInfo> Unlocked, string? Hint);

[JsonSerializable(typeof(PendingApprovalsResponse))]
[JsonSerializable(typeof(ApprovalDecisionRequest))]
[JsonSerializable(typeof(ApprovalDecisionResponse))]
[JsonSerializable(typeof(UnlockRequest))]
[JsonSerializable(typeof(UnlockResponse))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class RtfqApprovalJson : JsonSerializerContext;
