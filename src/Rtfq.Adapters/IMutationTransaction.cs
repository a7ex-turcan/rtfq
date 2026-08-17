using System.Text.Json.Nodes;

namespace Rtfq.Adapters;

/// <param name="MaxAffectedRows">
/// Reported back, not enforced here. The adapter executes and counts; the broker
/// decides whether the count is acceptable, so the cap stays policy and does not
/// become dialect knowledge.
/// </param>
/// <param name="StatementTimeout">Bounds the work an uncommitted runaway can do before we roll it back.</param>
/// <param name="LockTimeout">
/// Separate from the statement timeout and load-bearing for DDL: an ALTER waiting
/// on an exclusive lock queues every reader behind it, so a blocked schema change
/// takes the table down having changed nothing (ADR 0002).
/// </param>
public sealed record MutationOptions(
    int MaxAffectedRows,
    TimeSpan StatementTimeout,
    TimeSpan LockTimeout);

/// <summary>
/// A mutation that has run but is <b>not committed</b>.
///
/// This is the mechanism the whole write path rests on: the statement executes
/// inside a transaction, the driver reports the real number of rows it touched,
/// and only then does anything decide whether that was acceptable. An estimate
/// would not do — <c>EXPLAIN</c> lies, and the number that matters is the one the
/// engine actually produced.
///
/// Disposal rolls back. A handle that is dropped, expires, or is lost to a crash
/// therefore leaves nothing behind.
/// </summary>
public interface IMutationTransaction : IAsyncDisposable
{
    /// <summary>The driver's real count, read from the uncommitted execution.</summary>
    int AffectedRows { get; }

    /// <summary>
    /// The rows as they were before the statement ran, captured inside this same
    /// transaction. Empty for INSERT and for schema changes, which have no prior
    /// rows.
    /// </summary>
    JsonArray BeforeImages { get; }

    IReadOnlyList<Contracts.ColumnInfo> BeforeImageColumns { get; }

    /// <summary>True once committed or rolled back; a handle is single-use.</summary>
    bool IsSettled { get; }

    Task CommitAsync(CancellationToken cancellationToken);

    Task RollbackAsync(CancellationToken cancellationToken);
}
