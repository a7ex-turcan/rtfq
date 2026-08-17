using System.Text.Json.Nodes;
using Rtfq.Contracts;

namespace Rtfq.Adapters;

/// <param name="TransactionalWrites">Required before a source may declare <c>access: write</c>.</param>
/// <param name="TransactionalDdl">Required before a source may declare <c>access: schema</c> (ADR 0002).</param>
public sealed record SourceCapabilities(
    bool TransactionalWrites,
    bool TransactionalDdl,
    bool Explain,
    bool Introspection)
{
    public List<string> ToWire()
    {
        var caps = new List<string>();
        if (TransactionalWrites) caps.Add("transactional_writes");
        if (TransactionalDdl) caps.Add("transactional_ddl");
        if (Explain) caps.Add("explain");
        if (Introspection) caps.Add("introspection");
        return caps;
    }
}

public sealed record ReadOptions(int MaxRows, TimeSpan StatementTimeout);

/// <summary>
/// The outcome of a guard: a statement cleared to run, possibly rewritten to
/// carry a row limit.
///
/// Shared across dialects because the <i>shape</i> of guarding is shared even
/// though the parsing is not — every adapter validates, may bound, and returns
/// something safe to execute. If a dialect ever needs a different shape here,
/// that is a signal about the interface rather than a reason to widen this.
/// </summary>
/// <param name="Statement">The statement to execute, bounded where it was not already.</param>
/// <param name="Rewritten">Whether it differs from what the caller sent.</param>
public readonly record struct GuardedRead(string Statement, bool Rewritten);

public sealed record ReadResult(List<ColumnInfo> Columns, JsonArray Rows, int RowCount, bool Truncated);

/// <summary>
/// Raised for anything the caller or operator needs to distinguish. Carries an
/// <see cref="ErrorCodes"/> value so the server can answer without knowing which
/// driver threw — the adapter translates dialect and driver specifics here, and
/// nothing above this layer may inspect a provider exception.
/// </summary>
public sealed class AdapterException(string errorCode, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public string ErrorCode { get; } = errorCode;
}

/// <summary>
/// The extension point. Everything above this interface is source-agnostic; if
/// the core needs changing to accommodate one engine's quirk, this interface is
/// wrong and that should be said out loud rather than worked around.
///
/// The write half (classify, execute-in-transaction returning the real affected
/// count, commit, rollback) arrives in M3. It is deliberately absent rather than
/// stubbed, so no caller can mistake its presence for support.
/// </summary>
public interface ISourceAdapter : IAsyncDisposable
{
    string Name { get; }
    string Kind { get; }
    SourceCapabilities Capabilities { get; }

    /// <summary>
    /// Verifies the source is reachable and returns what it can <i>actually</i> do.
    ///
    /// Returns capabilities rather than void because some of them cannot be known
    /// without connecting: MongoDB supports transactions only on a replica set, and
    /// no amount of reading the config reveals the topology. <see cref="Capabilities"/>
    /// is the declared baseline; this is the observed truth, and the adapter
    /// updates the former from the latter.
    /// </summary>
    /// <exception cref="AdapterException">The source is not reachable.</exception>
    Task<SourceCapabilities> CheckAsync(CancellationToken cancellationToken);

    Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken);

    Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken);

    Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// The plan, without executing. The adapter builds the EXPLAIN itself rather
    /// than accepting one, so no caller can reach ANALYZE.
    /// </summary>
    Task<string> ExplainAsync(string statement, TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>
    /// Parses and classifies a statement without running it, so the gates above
    /// can decide before anything opens a transaction.
    /// </summary>
    /// <exception cref="AdapterException">The statement is refused outright.</exception>
    GuardedStatement Classify(string statement);

    /// <summary>
    /// Runs a mutation inside a transaction and leaves it <b>uncommitted</b>.
    /// Capturing before-images and reading the real affected-row count both
    /// happen inside that transaction.
    /// </summary>
    /// <exception cref="AdapterException">
    /// The source cannot do transactional writes, or the statement failed.
    /// </exception>
    Task<IMutationTransaction> BeginMutationAsync(
        GuardedStatement statement, MutationOptions options, CancellationToken cancellationToken);
}
