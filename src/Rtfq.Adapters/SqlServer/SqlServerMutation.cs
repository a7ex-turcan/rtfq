using System.Data;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Rtfq.Contracts;

namespace Rtfq.Adapters.SqlServer;

/// <summary>
/// A SQL Server mutation that has run and is waiting on a decision. Same shape
/// as the PostgreSQL one; the differences are dialect-level and stay here.
/// </summary>
internal sealed class SqlServerMutation : IMutationTransaction
{
    readonly SqlConnection _connection;
    readonly SqlTransaction _transaction;

    public int AffectedRows { get; }
    public JsonArray BeforeImages { get; }
    public IReadOnlyList<ColumnInfo> BeforeImageColumns { get; }
    public bool IsSettled { get; private set; }

    SqlServerMutation(SqlConnection connection, SqlTransaction transaction, int affected,
        JsonArray beforeImages, IReadOnlyList<ColumnInfo> columns)
    {
        _connection = connection;
        _transaction = transaction;
        AffectedRows = affected;
        BeforeImages = beforeImages;
        BeforeImageColumns = columns;
    }

    public static async Task<IMutationTransaction> BeginAsync(
        string connectionString, GuardedStatement statement, MutationOptions options, CancellationToken ct)
    {
        var connection = new SqlConnection(connectionString);
        SqlTransaction? transaction = null;

        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            // Repeatable read for the same reason as PostgreSQL: the before-image
            // SELECT and the mutation must see the same rows, or the journal
            // describes rows that were never changed.
            transaction = (SqlTransaction)await connection.BeginTransactionAsync(
                IsolationLevel.RepeatableRead, ct).ConfigureAwait(false);

            // LOCK_TIMEOUT is per-connection rather than per-transaction here, and
            // matters most for schema changes: a blocked ALTER holds up every
            // reader behind it (ADR 0002).
            await using (var lockTimeout = new SqlCommand(
                $"SET LOCK_TIMEOUT {(int)Math.Max(1, options.LockTimeout.TotalMilliseconds)}", connection, transaction))
            {
                await lockTimeout.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var (rows, columns) = statement.BeforeImageQuery is { } query
                ? await CaptureAsync(connection, transaction, query, options, ct).ConfigureAwait(false)
                : (new JsonArray(), []);

            await using var cmd = new SqlCommand(statement.Statement, connection, transaction)
            {
                CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 1,
            };

            var affected = await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return new SqlServerMutation(connection, transaction, affected, rows, columns);
        }
        catch
        {
            if (transaction is not null) await transaction.DisposeAsync().ConfigureAwait(false);
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    static async Task<(JsonArray Rows, IReadOnlyList<ColumnInfo> Columns)> CaptureAsync(
        SqlConnection connection, SqlTransaction transaction, string query, MutationOptions options, CancellationToken ct)
    {
        var rows = new JsonArray();
        var columns = new List<ColumnInfo>();

        await using var cmd = new SqlCommand(query, connection, transaction);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        for (var i = 0; i < reader.FieldCount; i++)
            columns.Add(new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i)));

        // One past the cap, so an over-cap mutation is recognisable from the
        // journal alone.
        var ceiling = options.MaxAffectedRows + 1;
        while (rows.Count < ceiling && await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new JsonArray();
            for (var i = 0; i < reader.FieldCount; i++) Append(row, Journal(reader, i));
            Append(rows, row);
        }

        return (rows, columns);
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
        IsSettled = true;
        await _transaction.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    static JsonNode? Journal(SqlDataReader reader, int i)
    {
        const int maxLength = 512;
        if (reader.IsDBNull(i)) return null;

        var type = reader.GetFieldType(i);
        if (type == typeof(bool)) return JsonValue.Create(reader.GetBoolean(i));
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
