namespace Rtfq.Server.Approval;

public enum ApprovalState
{
    Pending,
    Approved,
    Denied,

    /// <summary>Nobody answered in time. Treated exactly like a denial.</summary>
    Expired,
}

/// <param name="Statement">The statement itself, verbatim.</param>
/// <param name="DiffColumns">Columns of <paramref name="DiffRows"/>.</param>
/// <param name="DiffRows">The affected rows as they are now, serialized JSON.</param>
/// <remarks>
/// Deliberately contains no natural-language description of what the change
/// "does". Per CLAUDE.md principle 3, tool output must never influence policy,
/// and a summary supplied by the agent asking for approval is the most direct
/// route from a poisoned row to a human clicking yes. The approver sees the
/// statement and the data, or nothing.
/// </remarks>
public sealed record ApprovalContext(
    string Source,
    string TokenId,
    string Target,
    string Kind,
    string Statement,
    int? AffectedRows,
    IReadOnlyList<string> DiffColumns,
    string DiffRows,
    string Fingerprint);

/// <param name="Approver">Who decided. Recorded in the audit log alongside the statement.</param>
public sealed record ApprovalDecision(ApprovalState State, string? Approver, string? Reason);

/// <summary>
/// The seam between "a human must look at this" and *how* they are asked.
///
/// CLAUDE.md is explicit that Slack — the version people actually want — is a
/// plugin behind this interface and never in core. Two implementations ship, and
/// that is deliberate: one implementation is not a seam, it is an abstraction
/// nobody has tested. <see cref="LocalApprovalProvider"/> is the reference, and
/// <see cref="WebhookApprovalProvider"/> is how anything else gets built.
/// </summary>
public interface IApprovalProvider
{
    /// <summary>Human-readable name, for logs and for telling an operator what they are waiting on.</summary>
    string Name { get; }

    /// <summary>
    /// Asks for a decision. Called at propose time, not at commit time, so the
    /// approver has the whole window rather than discovering the request when the
    /// agent has already started waiting.
    /// </summary>
    Task<string> RequestAsync(ApprovalContext context, CancellationToken cancellationToken);

    /// <summary>The current decision. Never blocks: commit polls, it does not wait.</summary>
    Task<ApprovalDecision> DecisionAsync(string requestId, CancellationToken cancellationToken);

    /// <summary>Withdraws a request whose handle has gone away, so approvers are not asked about the dead.</summary>
    Task WithdrawAsync(string requestId, CancellationToken cancellationToken);
}
