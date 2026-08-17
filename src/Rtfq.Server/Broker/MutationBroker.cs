using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Rtfq.Adapters;
using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Broker;

/// <param name="Fingerprint">
/// Hash of the exact statement this handle came from. A handle cannot be
/// re-pointed — commit takes only the handle — and the fingerprint lets a caller
/// confirm it is committing what it proposed.
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

/// <summary>
/// Holds mutations between propose and commit.
///
/// Every entry is an open transaction on a real connection, which is why they are
/// counted, capped and expired. Per CLAUDE.md the two-phase execution is both the
/// safety mechanism and the counting mechanism: the statement runs, the driver
/// reports what it actually touched, and only then does anything decide whether
/// that was acceptable.
/// </summary>
public sealed class MutationBroker(
    RtfqConfig config,
    SourceRegistry sources,
    Audit.AuditLog audit,
    ILogger<MutationBroker> logger) : IAsyncDisposable
{
    /// <summary>
    /// Open handles allowed per source. Deliberately a constant rather than a
    /// config knob: every one is a held connection and a held lock set, and an
    /// operator raising this to escape a symptom would be making the problem
    /// worse. If four proposals are open at once, something upstream is wrong.
    /// </summary>
    public const int MaxOpenPerSource = 4;

    readonly ConcurrentDictionary<string, Entry> _open = new(StringComparer.Ordinal);
    readonly Timer _sweeper = null!;

    sealed record Entry(
        IMutationTransaction Transaction,
        string Source,
        string TokenId,
        string Statement,
        string Fingerprint,
        StatementKind Kind,
        string Target,
        DateTimeOffset ExpiresAt);

    public MutationBroker(
        RtfqConfig config, SourceRegistry sources, Audit.AuditLog audit,
        ILogger<MutationBroker> logger, bool startSweeper)
        : this(config, sources, audit, logger)
    {
        if (startSweeper)
            _sweeper = new Timer(_ => Sweep(), null, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5));
    }

    // --- propose -------------------------------------------------------------

    public async Task<WriteProposal> ProposeAsync(
        Policy.Caller caller, SourceSection source, string statement, CancellationToken cancellationToken)
    {
        var adapter = sources[source.Name];
        var guarded = adapter.Classify(statement);

        if (guarded.Kind == StatementKind.Read)
            throw new AdapterException(ErrorCodes.RequestMalformed,
                "this is a read; use query rather than propose_write");

        // Gate three: the specific target, and the deny rules over everything the
        // statement touches. Checked here, before anything opens a transaction.
        if (Policy.TargetPolicy.FirstDenied(source, guarded.Referenced) is { } denied)
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: '{denied}' is denied on this source");

        var outcome = Policy.TargetPolicy.EvaluateWrite(source, guarded.Target);
        if (outcome != TargetOutcomeAllowed)
        {
            var reason = outcome == Policy.TargetOutcome.Denied
                ? $"'{guarded.Target}' is denied on this source"
                : $"'{guarded.Target}' is not on the write allow-list for '{source.Name}'";
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {reason}");
        }

        var openForSource = _open.Values.Count(e => e.Source == source.Name);
        if (openForSource >= MaxOpenPerSource)
        {
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {MaxOpenPerSource} proposals are already open on '{source.Name}'. " +
                "Each holds a transaction; commit or abort one first.");
        }

        var cap = source.EffectiveMaxAffectedRows(config.Defaults);
        var options = new MutationOptions(
            cap,
            source.EffectiveStatementTimeout(config.Defaults),
            config.Defaults.LockTimeout);

        var transaction = await adapter.BeginMutationAsync(guarded, options, cancellationToken).ConfigureAwait(false);

        try
        {
            // The cap is enforced HERE, against the driver's real count from the
            // uncommitted execution — never an estimate, which is the whole reason
            // the statement runs before anything is decided.
            if (guarded.Kind == StatementKind.Mutation && transaction.AffectedRows > cap)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);

                audit.Write(new Audit.AuditEntry
                {
                    RequestId = "broker", Operation = "propose_write", TokenId = caller.TokenId,
                    Source = source.Name, Statement = statement, Classification = "refused",
                    Outcome = "error", ErrorCode = ErrorCodes.InsufficientAccess,
                    RowCount = transaction.AffectedRows,
                });

                throw new AdapterException(ErrorCodes.InsufficientAccess,
                    $"refused and rolled back: this would have changed {transaction.AffectedRows} rows, " +
                    $"and the cap for '{source.Name}' is {cap}");
            }

            var handle = Guid.NewGuid().ToString("n");
            var entry = new Entry(
                transaction, source.Name, caller.TokenId, statement, Fingerprint(statement),
                guarded.Kind, guarded.Target,
                DateTimeOffset.UtcNow + config.Defaults.WriteHandleTtl);

            _open[handle] = entry;

            audit.Write(new Audit.AuditEntry
            {
                RequestId = handle, Operation = "propose_write", TokenId = caller.TokenId,
                Source = source.Name, Statement = statement,
                Classification = guarded.Kind == StatementKind.Schema ? "schema" : "mutation",
                Outcome = "proposed",
                RowCount = guarded.Kind == StatementKind.Mutation ? transaction.AffectedRows : null,
                BeforeImages = transaction.BeforeImages.Count > 0 ? transaction.BeforeImages.ToJsonString() : null,
                SchemaSummary = guarded.SchemaSummary,
            });

            return new WriteProposal
            {
                Handle = handle,
                Source = source.Name,
                Kind = guarded.Kind,
                Target = guarded.Target,
                AffectedRows = guarded.Kind == StatementKind.Mutation ? transaction.AffectedRows : null,
                DiffColumns = [.. transaction.BeforeImageColumns],
                DiffSample = transaction.BeforeImages,
                RequiresApproval = source.RequireApproval,
                ExpiresAt = entry.ExpiresAt,
                Fingerprint = entry.Fingerprint,
                SchemaSummary = guarded.SchemaSummary,
            };
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    const Policy.TargetOutcome TargetOutcomeAllowed = Policy.TargetOutcome.Allowed;

    // --- settle ------------------------------------------------------------------

    public async Task<int?> CommitAsync(Policy.Caller caller, string handle, CancellationToken cancellationToken)
    {
        var entry = Claim(caller, handle);

        var source = config.FindSource(entry.Source);
        if (source?.RequireApproval == true)
        {
            // M4 introduces an approver. Until then this refuses rather than
            // pretending to queue something nobody will ever look at.
            await RollbackAsync(entry, "approval required").ConfigureAwait(false);
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused and rolled back: '{entry.Source}' requires human approval, and no approval provider " +
                "exists yet. Approval arrives in M4.");
        }

        await entry.Transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await entry.Transaction.DisposeAsync().ConfigureAwait(false);

        audit.Write(new Audit.AuditEntry
        {
            RequestId = handle, Operation = "commit_write", TokenId = caller.TokenId,
            Source = entry.Source, Statement = entry.Statement,
            Classification = entry.Kind == StatementKind.Schema ? "schema" : "mutation",
            Outcome = "committed", RowCount = entry.Transaction.AffectedRows,
        });

        return entry.Kind == StatementKind.Mutation ? entry.Transaction.AffectedRows : null;
    }

    public async Task AbortAsync(Policy.Caller caller, string handle, CancellationToken cancellationToken)
    {
        var entry = Claim(caller, handle);
        await RollbackAsync(entry, "aborted by caller").ConfigureAwait(false);

        audit.Write(new Audit.AuditEntry
        {
            RequestId = handle, Operation = "abort_write", TokenId = caller.TokenId,
            Source = entry.Source, Statement = entry.Statement,
            Classification = "mutation", Outcome = "aborted",
        });
    }

    /// <summary>
    /// Removes a handle and checks it belongs to this caller. Single-use by
    /// construction: it is gone from the dictionary before anything is settled, so
    /// a second commit finds nothing.
    /// </summary>
    Entry Claim(Policy.Caller caller, string handle)
    {
        if (!_open.TryRemove(handle, out var entry))
            throw new AdapterException(ErrorCodes.SourceUnknown,
                "no such handle: it may have been committed, aborted, or expired and rolled back");

        if (!string.Equals(entry.TokenId, caller.TokenId, StringComparison.Ordinal))
        {
            // Put it back: another caller's handle is not this caller's to consume.
            _open[handle] = entry;
            throw new AdapterException(ErrorCodes.SourceUnknown, "no such handle");
        }

        return entry;
    }

    async Task RollbackAsync(Entry entry, string why)
    {
        try
        {
            await entry.Transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await entry.Transaction.DisposeAsync().ConfigureAwait(false);
            logger.LogInformation("Rolled back mutation on {Source}: {Why}", entry.Source, why);
        }
    }

    // --- expiry -----------------------------------------------------------------------

    /// <summary>
    /// Rolls back anything past its TTL. An abandoned proposal must not hold a
    /// transaction open indefinitely — on PostgreSQL that also holds back VACUUM.
    /// </summary>
    public void Sweep()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var (handle, entry) in _open.ToArray())
        {
            if (entry.ExpiresAt > now) continue;
            if (!_open.TryRemove(handle, out _)) continue;

            _ = Task.Run(async () =>
            {
                await RollbackAsync(entry, "handle expired").ConfigureAwait(false);
                audit.Write(new Audit.AuditEntry
                {
                    RequestId = handle, Operation = "expire_write", TokenId = entry.TokenId,
                    Source = entry.Source, Statement = entry.Statement,
                    Classification = "mutation", Outcome = "expired",
                });
            });
        }
    }

    public int OpenCount => _open.Count;

    static string Fingerprint(string statement)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(statement));
        return Convert.ToHexStringLower(hash)[..16];
    }

    public async ValueTask DisposeAsync()
    {
        await (_sweeper?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(false);

        // Shutting down rolls back everything still open. Nothing half-decided
        // survives the process.
        foreach (var (handle, entry) in _open.ToArray())
        {
            _open.TryRemove(handle, out _);
            await RollbackAsync(entry, "server shutting down").ConfigureAwait(false);
        }
    }
}
