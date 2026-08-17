using System.Data;
using System.Text.Json.Nodes;
using Npgsql;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Postgres;

/// <summary>
/// A PostgreSQL mutation that has run and is waiting on a decision.
///
/// Holds an open connection and transaction until it is committed, aborted, or
/// disposed — which is why the broker caps how many can exist at once and why
/// they expire. An idle transaction is not free: it holds locks and, on
/// PostgreSQL, holds back <c>VACUUM</c>.
/// </summary>
internal sealed class PostgresMutation : IMutationTransaction
{
    readonly NpgsqlConnection _connection;
    readonly NpgsqlTransaction _transaction;

    public int AffectedRows { get; }
    public JsonArray BeforeImages { get; }
    public IReadOnlyList<ColumnInfo> BeforeImageColumns { get; }
    public bool IsSettled { get; private set; }

    PostgresMutation(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        int affectedRows,
        JsonArray beforeImages,
        IReadOnlyList<ColumnInfo> beforeImageColumns)
    {
        _connection = connection;
        _transaction = transaction;
        AffectedRows = affectedRows;
        BeforeImages = beforeImages;
        BeforeImageColumns = beforeImageColumns;
    }

    public static async Task<IMutationTransaction> BeginAsync(
        NpgsqlDataSource dataSource,
        GuardedStatement statement,
        MutationOptions options,
        CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        NpgsqlTransaction? transaction = null;

        try
        {
            // Repeatable read, deliberately. Under read committed each statement
            // takes a fresh snapshot, so the before-image SELECT and the mutation
            // could see different rows and the journal would describe rows that
            // were never changed. A serialization failure here is the correct
            // outcome — better a refused write than a wrong record of one.
            transaction = await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

            await ConfigureTimeoutsAsync(connection, transaction, options, cancellationToken).ConfigureAwait(false);

            var (beforeImages, columns) = statement.BeforeImageQuery is { } query
                ? await CaptureBeforeImagesAsync(connection, transaction, query, options, cancellationToken)
                    .ConfigureAwait(false)
                : (new JsonArray(), []);

            var affected = await ExecuteAsync(connection, transaction, statement, options, cancellationToken)
                .ConfigureAwait(false);

            return new PostgresMutation(connection, transaction, affected, beforeImages, columns);
        }
        catch
        {
            // Nothing is left open on a failed proposal.
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    static async Task ConfigureTimeoutsAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, MutationOptions options, CancellationToken ct)
    {
        // SET LOCAL, so both revert with the transaction rather than leaking onto
        // a pooled connection.
        var statementMs = (int)Math.Max(1, options.StatementTimeout.TotalMilliseconds);
        var lockMs = (int)Math.Max(1, options.LockTimeout.TotalMilliseconds);

        await using var cmd = new NpgsqlCommand(
            $"SET LOCAL statement_timeout = {statementMs}; SET LOCAL lock_timeout = {lockMs}",
            connection, transaction);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Captures the rows about to change, before changing them. Reads one past the
    /// cap so an over-cap mutation is still recognisable as over-cap from the
    /// journal alone.
    /// </summary>
    static async Task<(JsonArray Rows, IReadOnlyList<ColumnInfo> Columns)> CaptureBeforeImagesAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, string query,
        MutationOptions options, CancellationToken ct)
    {
        var rows = new JsonArray();
        var columns = new List<ColumnInfo>();

        await using var cmd = new NpgsqlCommand(query, connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i)));

        var ceiling = options.MaxAffectedRows + 1;
        while (rows.Count < ceiling && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new JsonArray();
            for (var i = 0; i < reader.FieldCount; i++)
                Append(row, Journal(reader, i));
            Append(rows, row);
        }

        return (rows, columns);
    }

    static async Task<int> ExecuteAsync(
        NpgsqlConnection connection, NpgsqlTransaction transaction, GuardedStatement statement,
        MutationOptions options, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(statement.Statement, connection, transaction)
        {
            CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 1,
        };

        // The driver's own count, from the real execution. Never an estimate.
        return await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    public async Task CommitAsync(CancellationToken cancellationToken)
    {
        if (IsSettled) throw new AdapterException(ErrorCodes.Internal, "this mutation has already been settled");
        IsSettled = true;
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken)
    {
        if (IsSettled) return;
        IsSettled = true;
        try { await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception) { /* the server may already have ended it; disposal still cleans up */ }
    }

    public async ValueTask DisposeAsync()
    {
        // Disposing an uncommitted transaction rolls it back. That is what makes a
        // dropped or expired handle safe by default rather than by remembering.
        IsSettled = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    /// <summary>
    /// Renders a value for the journal, truncating with a marker.
    ///
    /// A before-image row can contain a large jsonb document or a megabyte of
    /// text, and a journal that grows without bound is one nobody keeps. Losing
    /// the tail of one value is recoverable; losing the audit log is not.
    /// </summary>
    static JsonNode? Journal(NpgsqlDataReader reader, int i)
    {
        const int maxLength = 512;

        if (reader.IsDBNull(i)) return null;

        var type = reader.GetFieldType(i);
        if (type == typeof(bool)) return JsonValue.Create(reader.GetBoolean(i));
        if (type == typeof(short)) return JsonValue.Create(reader.GetInt16(i));
        if (type == typeof(int)) return JsonValue.Create(reader.GetInt32(i));
        if (type == typeof(long)) return JsonValue.Create(reader.GetInt64(i));
        if (type == typeof(double)) return JsonValue.Create(reader.GetDouble(i));
        if (type == typeof(decimal)) return JsonValue.Create(reader.GetDecimal(i));
        if (type == typeof(DateTime)) return JsonValue.Create(reader.GetDateTime(i).ToString("O"));

        var text = reader.GetValue(i).ToString() ?? "";
        return JsonValue.Create(text.Length <= maxLength
            ? text
            : text[..maxLength] + $"…[truncated, {text.Length} chars]");
    }
}
