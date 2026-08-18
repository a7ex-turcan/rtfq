using System.Collections.Concurrent;

namespace Rtfq.Server.Approval;

/// <param name="RequestedAt">When the approver was asked, so a queue can be ordered oldest-first.</param>
public sealed record PendingApproval(
    string Id,
    ApprovalContext Context,
    DateTimeOffset RequestedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// The reference approval provider: an in-memory queue this server serves over
/// its own API, which <c>rtfq approvals</c> reads and answers.
///
/// In memory on purpose. A pending approval is only meaningful while the handle
/// it belongs to is alive, and handles do not survive a restart, so persisting
/// approvals would leave an operator approving something that no longer exists.
/// Restarting clears the queue, which is the honest behaviour.
/// </summary>
public sealed class LocalApprovalProvider(TimeSpan window) : IApprovalProvider
{
    readonly ConcurrentDictionary<string, Entry> _requests = new(StringComparer.Ordinal);

    sealed record Entry(PendingApproval Pending, ApprovalDecision Decision);

    public string Name => "local";

    public Task<string> RequestAsync(ApprovalContext context, CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid().ToString("n")[..12];
        var now = DateTimeOffset.UtcNow;

        _requests[id] = new Entry(
            new PendingApproval(id, context, now, now + window),
            new ApprovalDecision(ApprovalState.Pending, null, null));

        return Task.FromResult(id);
    }

    public Task<ApprovalDecision> DecisionAsync(string requestId, CancellationToken cancellationToken)
    {
        if (!_requests.TryGetValue(requestId, out var entry))
        {
            // A request nobody can find is not an approval. Failing closed here
            // matters more than any diagnostic nicety.
            return Task.FromResult(new ApprovalDecision(ApprovalState.Denied, null, "the approval request is gone"));
        }

        if (entry.Decision.State == ApprovalState.Pending && DateTimeOffset.UtcNow > entry.Pending.ExpiresAt)
            return Task.FromResult(new ApprovalDecision(ApprovalState.Expired, null, "nobody answered in time"));

        return Task.FromResult(entry.Decision);
    }

    public Task WithdrawAsync(string requestId, CancellationToken cancellationToken)
    {
        _requests.TryRemove(requestId, out _);
        return Task.CompletedTask;
    }

    // --- the approver side ----------------------------------------------------

    /// <summary>Everything still waiting on a human, oldest first.</summary>
    public IReadOnlyList<PendingApproval> Pending()
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            .. _requests.Values
                .Where(e => e.Decision.State == ApprovalState.Pending && e.Pending.ExpiresAt > now)
                .Select(e => e.Pending)
                .OrderBy(p => p.RequestedAt),
        ];
    }

    /// <summary>
    /// Records a decision. Returns false if the request is unknown or already
    /// decided: an approval is not something to change your mind about once the
    /// commit may already have acted on it.
    /// </summary>
    public bool Decide(string requestId, bool approved, string approver, string? reason)
    {
        if (!_requests.TryGetValue(requestId, out var entry)) return false;
        if (entry.Decision.State != ApprovalState.Pending) return false;

        var decision = new ApprovalDecision(
            approved ? ApprovalState.Approved : ApprovalState.Denied, approver, reason);

        return _requests.TryUpdate(requestId, entry with { Decision = decision }, entry);
    }
}
