using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Rtfq.Adapters;
using Rtfq.Contracts;
using Rtfq.Server.Approval;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Broker;

/// <param name="Fingerprint">
/// Hash of the statement together with the diff it produced. A handle cannot be
/// re-pointed, because commit takes only the handle; and for an approval-required
/// source this is also what binds a human's yes to the exact change they saw.
/// </param>
public sealed record WriteProposal
{
    public required string Handle { get; init; }
    public required string Source { get; init; }
    public required StatementKind Kind { get; init; }
    public required string Target { get; init; }
    public int? AffectedRows { get; init; }
    public required List<ColumnInfo> DiffColumns { get; init; }
    public required JsonArray DiffSample { get; init; }
    public required bool RequiresApproval { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required string Fingerprint { get; init; }
    public string? SchemaSummary { get; init; }
}

public sealed record CommitOutcome(string State, int? AffectedRows, string? Approver, string? Detail);

/// <summary>
/// Holds mutations between propose and commit.
///
/// Two shapes, and the difference is the whole of M4. Without approval a handle
/// holds an open transaction: fastest, strictly serialisable, settled in
/// milliseconds. With approval it holds NOTHING. The statement runs, the diff is
/// captured, and it rolls straight back, because a transaction left open while a
/// human decides blocks readers on SQL Server and holds back VACUUM on
/// PostgreSQL for as long as the person takes.
///
/// The cost of not holding is that the world can move underneath an approval, so
/// commit re-runs the statement and refuses unless the diff is identical to the
/// one that was approved.
/// </summary>
public sealed class MutationBroker : IAsyncDisposable
{
    /// <summary>
    /// Open handles allowed per source. Deliberately a constant rather than a
    /// config knob: each is a held connection and a held lock set, and an operator
    /// raising this to escape a symptom would be making the problem worse.
    /// </summary>
    public const int MaxOpenPerSource = 4;

    readonly RtfqConfig _config;
    readonly SourceRegistry _sources;
    readonly Audit.AuditLog _audit;
    readonly ILogger<MutationBroker> _logger;
    readonly IApprovalProvider _approvals;
    readonly Policy.UnlockStore _unlocks;
    readonly ConcurrentDictionary<string, Entry> _open = new(StringComparer.Ordinal);
    readonly Timer? _sweeper;

    /// <param name="Transaction">Live for an ordinary proposal; null while an approval is outstanding.</param>
    /// <param name="ApprovalId">Set only when a human has been asked.</param>
    sealed record Entry(
        IMutationTransaction? Transaction,
        GuardedStatement Guarded,
        string Source,
        string TokenId,
        string Fingerprint,
        int? AffectedRows,
        JsonArray DiffSample,
        IReadOnlyList<ColumnInfo> DiffColumns,
        string? ApprovalId,
        DateTimeOffset ExpiresAt);

    public MutationBroker(
        RtfqConfig config, SourceRegistry sources, Audit.AuditLog audit, ILogger<MutationBroker> logger,
        IApprovalProvider approvals, Policy.UnlockStore unlocks, bool startSweeper = false)
    {
        _config = config;
        _sources = sources;
        _audit = audit;
        _logger = logger;
        _approvals = approvals;
        _unlocks = unlocks;

        if (startSweeper)
            _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    // --- propose -------------------------------------------------------------

    public async Task<WriteProposal> ProposeAsync(
        Policy.Caller caller, SourceSection source, string statement, CancellationToken cancellationToken)
    {
        var adapter = _sources[source.Name];
        var guarded = adapter.Classify(statement);

        if (guarded.Kind == StatementKind.Read)
            throw new AdapterException(ErrorCodes.RequestMalformed, "this is a read; use query rather than propose_write");

        var required = guarded.Kind == StatementKind.Schema ? AccessLevel.Schema : AccessLevel.Write;

        // The unlock gate sits ahead of the target checks: a locked source should
        // say it is locked, not report which tables it would otherwise have allowed.
        if (source.RequireUnlock && !_unlocks.IsUnlocked(source.Name, required))
        {
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: '{source.Name}' is locked. Run: rtfq unlock {source.Name} --write --ttl 15m");
        }

        if (Policy.TargetPolicy.FirstDenied(source, guarded.Referenced) is { } denied)
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: '{denied}' is denied on this source");

        var outcome = Policy.TargetPolicy.EvaluateWrite(source, guarded.Target);
        if (outcome != Policy.TargetOutcome.Allowed)
        {
            var reason = outcome == Policy.TargetOutcome.Denied
                ? $"'{guarded.Target}' is denied on this source"
                : $"'{guarded.Target}' is not on the write allow-list for '{source.Name}'";
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {reason}");
        }

        if (_open.Values.Count(e => e.Source == source.Name) >= MaxOpenPerSource)
        {
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {MaxOpenPerSource} proposals are already open on '{source.Name}'. " +
                "Each holds a transaction; commit or abort one first.");
        }

        var cap = source.EffectiveMaxAffectedRows(_config.Defaults);
        var options = new MutationOptions(cap, source.EffectiveStatementTimeout(_config.Defaults), _config.Defaults.LockTimeout);

        var transaction = await adapter.BeginMutationAsync(guarded, options, cancellationToken).ConfigureAwait(false);
        var keepTransaction = false;

        try
        {
            // Enforced against the driver's real count from the uncommitted
            // execution, never an estimate.
            if (guarded.Kind == StatementKind.Mutation && transaction.AffectedRows > cap)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                Journal("propose_write", "broker", caller.TokenId, source.Name, statement, "refused", "error",
                    ErrorCodes.InsufficientAccess, transaction.AffectedRows, null, null);

                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    $"refused and rolled back: this would have changed {transaction.AffectedRows} rows, " +
                    $"and the cap for '{source.Name}' is {cap}");
            }

            var affected = guarded.Kind == StatementKind.Mutation ? transaction.AffectedRows : (int?)null;
            var diff = transaction.BeforeImages;
            var columns = transaction.BeforeImageColumns;
            var fingerprint = Fingerprint(statement, affected, diff);

            var handle = Guid.NewGuid().ToString("n");
            string? approvalId = null;
            var ttl = _config.Defaults.WriteHandleTtl;

            if (source.RequireApproval)
            {
                // Nothing is held while a human decides. Roll back now; commit
                // re-runs and checks the diff still matches.
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);

                approvalId = await _approvals.RequestAsync(new ApprovalContext(
                    source.Name, caller.TokenId, guarded.Target,
                    guarded.Kind == StatementKind.Schema ? "schema" : "mutation",
                    statement, affected, [.. columns.Select(c => c.Name)], diff.ToJsonString(), fingerprint),
                    cancellationToken).ConfigureAwait(false);

                // A human needs longer than a machine. Affordable precisely
                // because no transaction is being held open to pay for it.
                ttl = _config.Defaults.ApprovalTtl;
            }
            else
            {
                keepTransaction = true;
            }

            _open[handle] = new Entry(
                keepTransaction ? transaction : null, guarded, source.Name, caller.TokenId,
                fingerprint, affected, diff, columns, approvalId, DateTimeOffset.UtcNow + ttl);

            Journal("propose_write", handle, caller.TokenId, source.Name, statement,
                guarded.Kind == StatementKind.Schema ? "schema" : "mutation",
                source.RequireApproval ? "awaiting-approval" : "proposed",
                null, affected, diff.Count > 0 ? diff.ToJsonString() : null, guarded.SchemaSummary);

            return new WriteProposal
            {
                Handle = handle,
                Source = source.Name,
                Kind = guarded.Kind,
                Target = guarded.Target,
                AffectedRows = affected,
                DiffColumns = [.. columns],
                DiffSample = diff,
                RequiresApproval = source.RequireApproval,
                ExpiresAt = DateTimeOffset.UtcNow + ttl,
                Fingerprint = fingerprint,
                SchemaSummary = guarded.SchemaSummary,
            };
        }
        finally
        {
            if (!keepTransaction && !transaction.IsSettled)
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // --- settle ------------------------------------------------------------------

    public async Task<CommitOutcome> CommitAsync(Policy.Caller caller, string handle, CancellationToken cancellationToken)
    {
        var entry = Claim(caller, handle);

        if (entry.ApprovalId is null)
        {
            await entry.Transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
            await entry.Transaction.DisposeAsync().ConfigureAwait(false);

            Journal("commit_write", handle, caller.TokenId, entry.Source, entry.Guarded.Statement,
                Classification(entry), "committed", null, entry.AffectedRows, null, null);

            return new CommitOutcome("committed", entry.AffectedRows, null, null);
        }

        return await CommitApprovedAsync(caller, handle, entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Commits something a human was asked about. Re-runs the statement and
    /// refuses unless the diff is identical to the one approved, because in the
    /// interval the approver was thinking, the rows may have moved.
    /// </summary>
    async Task<CommitOutcome> CommitApprovedAsync(
        Policy.Caller caller, string handle, Entry entry, CancellationToken cancellationToken)
    {
        var decision = await _approvals.DecisionAsync(entry.ApprovalId!, cancellationToken).ConfigureAwait(false);

        if (decision.State == ApprovalState.Pending)
        {
            // Still waiting: give the handle back rather than consuming it.
            _open[handle] = entry;
            return new CommitOutcome("pending", entry.AffectedRows, null,
                decision.Reason ?? "nobody has decided yet");
        }

        if (decision.State != ApprovalState.Approved)
        {
            await _approvals.WithdrawAsync(entry.ApprovalId!, cancellationToken).ConfigureAwait(false);
            Journal("commit_write", handle, caller.TokenId, entry.Source, entry.Guarded.Statement,
                Classification(entry), decision.State.ToString().ToLowerInvariant(),
                ErrorCodes.InsufficientAccess, null, null, null, decision.Approver);

            var why = decision.State == ApprovalState.Expired
                ? "nobody approved this in time, so the request has lapsed"
                : "the change was denied" + (decision.Approver is { } who ? $" by {who}" : "");

            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {why}" +
                (decision.Reason is { } note ? $": {note}" : "") +
                ". Nothing was changed.");
        }

        var source = _config.FindSource(entry.Source)
                     ?? throw new AdapterException(ErrorCodes.SourceUnknown, $"no source '{entry.Source}'");

        var cap = source.EffectiveMaxAffectedRows(_config.Defaults);
        var options = new MutationOptions(cap, source.EffectiveStatementTimeout(_config.Defaults), _config.Defaults.LockTimeout);

        var transaction = await _sources[entry.Source]
            .BeginMutationAsync(entry.Guarded, options, cancellationToken).ConfigureAwait(false);

        try
        {
            var affected = entry.Guarded.Kind == StatementKind.Mutation ? transaction.AffectedRows : (int?)null;
            var fingerprint = Fingerprint(entry.Guarded.Statement, affected, transaction.BeforeImages);

            if (!string.Equals(fingerprint, entry.Fingerprint, StringComparison.Ordinal))
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                Journal("commit_write", handle, caller.TokenId, entry.Source, entry.Guarded.Statement,
                    Classification(entry), "stale", ErrorCodes.InsufficientAccess, affected, null, null, decision.Approver);

                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    "refused and rolled back: the data changed after this was approved, so the approval no longer " +
                    "describes it. Propose it again and have the new diff approved.");
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

            Journal("commit_write", handle, caller.TokenId, entry.Source, entry.Guarded.Statement,
                Classification(entry), "committed", null, affected,
                transaction.BeforeImages.Count > 0 ? transaction.BeforeImages.ToJsonString() : null,
                entry.Guarded.SchemaSummary, decision.Approver);

            return new CommitOutcome("committed", affected, decision.Approver, null);
        }
        finally
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task AbortAsync(Policy.Caller caller, string handle, CancellationToken cancellationToken)
    {
        var entry = Claim(caller, handle);
        await ReleaseAsync(entry, "aborted by caller").ConfigureAwait(false);

        Journal("abort_write", handle, caller.TokenId, entry.Source, entry.Guarded.Statement,
            Classification(entry), "aborted", null, null, null, null);
    }

    /// <summary>
    /// Removes a handle and checks it belongs to this caller. Single-use by
    /// construction: it is gone from the dictionary before anything is settled.
    /// </summary>
    Entry Claim(Policy.Caller caller, string handle)
    {
        if (!_open.TryRemove(handle, out var entry))
            throw new AdapterException(ErrorCodes.SourceUnknown,
                "no such handle: it may have been committed, aborted, or expired and rolled back");

        if (!string.Equals(entry.TokenId, caller.TokenId, StringComparison.Ordinal))
        {
            // Another caller's handle is not this caller's to consume, and its
            // existence is not this caller's to learn.
            _open[handle] = entry;
            throw new AdapterException(ErrorCodes.SourceUnknown, "no such handle");
        }

        return entry;
    }

    async Task ReleaseAsync(Entry entry, string why)
    {
        if (entry.ApprovalId is not null)
            await _approvals.WithdrawAsync(entry.ApprovalId, CancellationToken.None).ConfigureAwait(false);

        if (entry.Transaction is null) return;

        try { await entry.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            await entry.Transaction.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("Released mutation on {Source}: {Why}", entry.Source, why);
        }
    }

    // --- expiry -----------------------------------------------------------------------

    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (handle, entry) in _open.ToArray())
        {
            if (entry.ExpiresAt > now) continue;
            if (!_open.TryRemove(handle, out _)) continue;

            _ = Task.Run(async () =>
            {
                await ReleaseAsync(entry, "handle expired").ConfigureAwait(false);
                Journal("expire_write", handle, entry.TokenId, entry.Source, entry.Guarded.Statement,
                    Classification(entry), "expired", null, null, null, null);
            });
        }
    }

    public int OpenCount => _open.Count;

    // --- helpers ---------------------------------------------------------------------------

    static string Classification(Entry entry) => entry.Guarded.Kind == StatementKind.Schema ? "schema" : "mutation";

    /// <summary>
    /// Identifies a change by what it would do, not merely by what it says. The
    /// statement alone would let identical SQL over different data pass as the
    /// same approved change.
    /// </summary>
    static string Fingerprint(string statement, int? affected, JsonArray diff)
    {
        var material = $"{statement} {affected?.ToString() ?? "-"} {diff.ToJsonString()}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16];
    }

    void Journal(string operation, string requestId, string tokenId, string source, string statement,
        string classification, string outcome, string? errorCode, int? rowCount,
        string? beforeImages, string? schemaSummary, string? approver = null)
    {
        _audit.Write(new Audit.AuditEntry
        {
            RequestId = requestId,
            Operation = operation,
            TokenId = tokenId,
            Source = source,
            Statement = statement,
            Classification = classification,
            Outcome = outcome,
            ErrorCode = errorCode,
            RowCount = rowCount,
            BeforeImages = beforeImages,
            SchemaSummary = schemaSummary,
            Approver = approver,
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (_sweeper is not null) await _sweeper.DisposeAsync().ConfigureAwait(false);

        // Shutting down releases everything still open. Nothing half-decided
        // survives the process.
        foreach (var (handle, entry) in _open.ToArray())
        {
            _open.TryRemove(handle, out _);
            await ReleaseAsync(entry, "server shutting down").ConfigureAwait(false);
        }
    }
}
