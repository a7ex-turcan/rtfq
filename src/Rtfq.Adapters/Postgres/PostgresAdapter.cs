using System.Data;
using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Postgres;

/// <summary>
/// PostgreSQL via Npgsql.
///
/// Built on <see cref="NpgsqlSlimDataSourceBuilder"/> rather than the standard
/// builder: the standard one discovers type plugins reflectively, which the
/// trimmer cannot follow and NativeAOT therefore cannot guarantee.
/// </summary>
public sealed class PostgresAdapter : ISourceAdapter
{
    readonly NpgsqlDataSource _dataSource;

    public string Name { get; }
    public string Kind => "postgres";

    public SourceCapabilities Capabilities { get; } = new(
        TransactionalWrites: true,
        TransactionalDdl: true,   // PostgreSQL rolls back DDL; this is what lets access: schema exist
        Explain: true,
        Introspection: true);

    public PostgresAdapter(string name, string connectionString, IReadOnlyList<string> schemas, TimeSpan statementTimeout)
    {
        Name = name;

        NpgsqlConnectionStringBuilder csb;
        try
        {
            csb = new NpgsqlConnectionStringBuilder(connectionString);
        }
        catch (ArgumentException ex)
        {
            throw new AdapterException(ErrorCodes.ConfigInvalid, $"source '{name}' has an unparseable dsn: {ex.Message}", ex);
        }

        // Pin search_path rather than inheriting the role's default. An allow-list
        // entry checked against a name the server resolves differently is a gate
        // bypass, so unqualified names must resolve to what config says (ADR 0001).
        if (schemas.Count > 0)
            csb.SearchPath = string.Join(',', schemas);

        // Server-side statement_timeout is the blast-radius guard; the client-side
        // command timeout alone would leave the query running after we stopped
        // waiting for it.
        var timeoutMs = (int)Math.Max(1, statementTimeout.TotalMilliseconds);
        csb.Options = string.IsNullOrEmpty(csb.Options)
            ? $"-c statement_timeout={timeoutMs}"
            : $"{csb.Options} -c statement_timeout={timeoutMs}";

        csb.CommandTimeout = (int)Math.Ceiling(statementTimeout.TotalSeconds) + 1;

        _dataSource = new NpgsqlSlimDataSourceBuilder(csb.ConnectionString).Build();
    }

    public async Task CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public async Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT table_schema, table_name, table_type
            FROM information_schema.tables
            WHERE table_schema NOT IN ('pg_catalog', 'information_schema')
              AND (current_setting('search_path') = '"$user", public' OR table_schema = ANY (current_schemas(false)))
            ORDER BY table_schema, table_name
            """;

        var tables = new List<TableInfo>();
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tables.Add(new TableInfo(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2) == "VIEW" ? "view" : "table"));
            }
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        return new SchemaSnapshot(tables, DateTimeOffset.UtcNow);
    }

    public Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken)
    {
        // The identifier comes from config or an already-authorised caller, but it
        // is still quoted rather than interpolated: quoting is the habit that keeps
        // the one place it matters from being the place it was forgotten.
        var sql = $"SELECT * FROM {QuoteQualified(table)} LIMIT {rows}";
        return ExecuteReadAsync(sql, new ReadOptions(rows, TimeSpan.FromSeconds(15)), cancellationToken);
    }

    public async Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken)
    {
        var columns = new List<ColumnInfo>();
        var rows = new JsonArray();
        var truncated = false;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(statement, conn)
            {
                CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 1,
            };

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                                                .ConfigureAwait(false);

            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i)));

            // M0 caps by stopping the scan at max_rows + 1: reading one row beyond
            // the cap is what distinguishes "exactly full" from "there was more".
            // Real LIMIT injection needs the parser and lands in M1.
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count >= options.MaxRows)
                {
                    truncated = true;
                    break;
                }

                var row = new JsonArray();
                for (var i = 0; i < reader.FieldCount; i++)
                    Append(row, ToJson(reader, i));
                Append(rows, row);
            }
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        return new ReadResult(columns, rows, rows.Count, truncated);
    }

    /// <summary>
    /// Appends through <see cref="IList{T}"/> rather than <c>JsonArray.Add&lt;T&gt;</c>.
    /// The generic overload is annotated RequiresDynamicCode and would make the
    /// whole project fail its AOT check — correctly, since it boxes into a
    /// JsonValue whose type the trimmer cannot see.
    /// </summary>
    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    /// <summary>
    /// Converts a cell to a JSON value by its declared type. Explicit rather than
    /// reflective, so nothing here depends on metadata the trimmer may remove.
    /// </summary>
    static JsonNode? ToJson(NpgsqlDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;

        var type = reader.GetFieldType(i);

        if (type == typeof(bool)) return JsonValue.Create(reader.GetBoolean(i));
        if (type == typeof(short)) return JsonValue.Create(reader.GetInt16(i));
        if (type == typeof(int)) return JsonValue.Create(reader.GetInt32(i));
        if (type == typeof(long)) return JsonValue.Create(reader.GetInt64(i));
        if (type == typeof(float)) return JsonValue.Create(reader.GetFloat(i));
        if (type == typeof(double)) return JsonValue.Create(reader.GetDouble(i));
        if (type == typeof(decimal)) return JsonValue.Create(reader.GetDecimal(i));
        if (type == typeof(string)) return JsonValue.Create(reader.GetString(i));
        if (type == typeof(Guid)) return JsonValue.Create(reader.GetGuid(i).ToString());
        if (type == typeof(DateTime)) return JsonValue.Create(reader.GetDateTime(i).ToString("O"));
        if (type == typeof(DateTimeOffset)) return JsonValue.Create(reader.GetFieldValue<DateTimeOffset>(i).ToString("O"));
        if (type == typeof(byte[])) return JsonValue.Create(Convert.ToBase64String(reader.GetFieldValue<byte[]>(i)));

        // Anything exotic (json, arrays, ranges, custom types) renders as text
        // rather than failing the whole query. M1 revisits this with the schema
        // cache, where we will know the column's real shape ahead of time.
        return JsonValue.Create(reader.GetValue(i).ToString());
    }

    static string QuoteQualified(string identifier)
    {
        var sb = new StringBuilder();
        foreach (var part in identifier.Split('.'))
        {
            if (sb.Length > 0) sb.Append('.');
            sb.Append('"').Append(part.Replace("\"", "\"\"", StringComparison.Ordinal)).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Maps driver failures onto the stable error taxonomy. The distinction a
    /// caller needs is "my statement was wrong" versus "the source is not
    /// answering", so those must not collapse into one code.
    /// </summary>
    static AdapterException Translate(Exception ex) => ex switch
    {
        AdapterException adapter => adapter,

        // 57014 = query_canceled, which is what statement_timeout raises.
        PostgresException { SqlState: "57014" } pg
            => new AdapterException(ErrorCodes.SourceTimeout, "statement exceeded statement_timeout", pg),

        PostgresException pg
            => new AdapterException(ErrorCodes.SourceRejected, $"{pg.MessageText} (SQLSTATE {pg.SqlState})", pg),

        NpgsqlException or TimeoutException
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {ex.Message}", ex),

        OperationCanceledException
            => new AdapterException(ErrorCodes.SourceTimeout, "the request was cancelled before the source answered", ex),

        _ => new AdapterException(ErrorCodes.Internal, ex.Message, ex),
    };

    public async ValueTask DisposeAsync() => await _dataSource.DisposeAsync().ConfigureAwait(false);
}
