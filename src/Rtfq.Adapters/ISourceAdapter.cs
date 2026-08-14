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

public sealed record ReadResult(List<ColumnInfo> Columns, JsonArray Rows, int RowCount, bool Truncated);

public sealed record TableInfo(string Schema, string Name, string Kind);

public sealed record SchemaSnapshot(List<TableInfo> Tables, DateTimeOffset CapturedAt);

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

    /// <summary>Verifies the source is reachable. Throws <see cref="AdapterException"/> if not.</summary>
    Task CheckAsync(CancellationToken cancellationToken);

    Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken);

    Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken);

    Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken);
}
