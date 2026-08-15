using System.Data;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.SqlClient;
using Rtfq.Contracts;

namespace Rtfq.Adapters.SqlServer;

/// <summary>
/// Microsoft SQL Server via <c>Microsoft.Data.SqlClient</c>.
///
/// SQL Server supports transactional DDL, which is why it may declare
/// <c>access: schema</c> (ADR 0002).
/// </summary>
public sealed class SqlServerAdapter : ISourceAdapter
{
    readonly string _connectionString;
    readonly string[] _schemas;
    readonly TimeSpan _statementTimeout;

    public string Name { get; }
    public string Kind => "mssql";

    public SourceCapabilities Capabilities { get; private set; } = new(
        TransactionalWrites: true,
        TransactionalDdl: true,
        Explain: true,
        Introspection: true);

    public SqlServerAdapter(string name, string connectionString, IReadOnlyList<string> schemas, TimeSpan statementTimeout)
    {
        Name = name;
        _schemas = [.. schemas];
        _statementTimeout = statementTimeout;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString)
            {
                CommandTimeout = (int)Math.Ceiling(statementTimeout.TotalSeconds) + 1,
            };
            _connectionString = builder.ConnectionString;
        }
        catch (ArgumentException ex)
        {
            throw new AdapterException(ErrorCodes.ConfigInvalid, $"source '{name}' has an unparseable dsn: {ex.Message}", ex);
        }
    }

    public async Task<SourceCapabilities> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand("SELECT 1", conn);
            await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return Capabilities;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    async Task<SqlConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        return conn;
    }

    // --- introspection ------------------------------------------------------

    public async Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);

            var tables = await ReadTablesAsync(conn, cancellationToken).ConfigureAwait(false);
            await ReadColumnsAsync(conn, tables, cancellationToken).ConfigureAwait(false);
            await ReadIndexesAsync(conn, tables, cancellationToken).ConfigureAwait(false);
            await ReadForeignKeysAsync(conn, tables, cancellationToken).ConfigureAwait(false);

            return new SchemaSnapshot
            {
                Source = Name,
                CapturedAt = DateTimeOffset.UtcNow,
                Tables = [.. tables.Values.OrderBy(t => t.Schema, StringComparer.Ordinal)
                                          .ThenBy(t => t.Name, StringComparer.Ordinal)],
            };
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    // Row counts come from sys.partitions, which is maintained by the engine.
    // COUNT(*) over every table would be an introspection pass that table-scans
    // the whole database.
    async Task<Dictionary<string, TableSchema>> ReadTablesAsync(SqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, t.name, 'table',
                   (SELECT TOP 1 p.rows FROM sys.partitions p
                    WHERE p.object_id = t.object_id AND p.index_id IN (0,1))
            FROM sys.tables t JOIN sys.schemas s ON s.schema_id = t.schema_id
            UNION ALL
            SELECT s.name, v.name, 'view', NULL
            FROM sys.views v JOIN sys.schemas s ON s.schema_id = v.schema_id
            """;

        var tables = new Dictionary<string, TableSchema>(StringComparer.Ordinal);

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var schema = reader.GetString(0);
            if (_schemas.Length > 0 && !_schemas.Contains(schema, StringComparer.Ordinal)) continue;

            var table = new TableSchema
            {
                Schema = schema,
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                EstimatedRows = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Columns = [],
            };
            tables[table.QualifiedName] = table;
        }

        return tables;
    }

    async Task ReadColumnsAsync(SqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT c.TABLE_SCHEMA, c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE,
                   c.CHARACTER_MAXIMUM_LENGTH, c.NUMERIC_PRECISION, c.NUMERIC_SCALE,
                   c.IS_NULLABLE, c.COLUMN_DEFAULT
            FROM INFORMATION_SCHEMA.COLUMNS c
            ORDER BY c.TABLE_SCHEMA, c.TABLE_NAME, c.ORDINAL_POSITION
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;

            table.Columns.Add(new ColumnSchema
            {
                Name = reader.GetString(2),
                Type = RenderType(reader),
                Nullable = reader.GetString(7) == "YES",
                Default = reader.IsDBNull(8) ? null : reader.GetString(8),
            });
        }
    }

    /// <summary>
    /// Rebuilds the declared type from its parts, so a column reads as
    /// <c>nvarchar(200)</c> rather than a bare <c>nvarchar</c> — the length is
    /// exactly what an agent needs to know before writing a value.
    /// </summary>
    static string RenderType(SqlDataReader reader)
    {
        var name = reader.GetString(3);

        if (!reader.IsDBNull(4))
        {
            var length = reader.GetInt32(4);
            return length < 0 ? $"{name}(max)" : $"{name}({length})";
        }

        if (name is "decimal" or "numeric" && !reader.IsDBNull(5))
        {
            var precision = reader.GetByte(5);
            var scale = reader.IsDBNull(6) ? 0 : reader.GetInt32(6);
            return $"{name}({precision},{scale})";
        }

        return name;
    }

    async Task ReadIndexesAsync(SqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, t.name, i.name, i.is_unique, i.is_primary_key,
                   STRING_AGG(c.name, ',') WITHIN GROUP (ORDER BY ic.key_ordinal)
            FROM sys.indexes i
            JOIN sys.tables t  ON t.object_id = i.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
            JOIN sys.columns c ON c.object_id = i.object_id AND c.column_id = ic.column_id
            WHERE i.name IS NOT NULL AND ic.is_included_column = 0
            GROUP BY s.name, t.name, i.name, i.is_unique, i.is_primary_key
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;

            var columns = reader.GetString(5).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            var primary = reader.GetBoolean(4);

            table.Indexes.Add(new IndexSchema
            {
                Name = reader.GetString(2),
                Columns = columns,
                Unique = reader.GetBoolean(3),
                Primary = primary,
            });

            if (primary) table.PrimaryKey.AddRange(columns);
        }
    }

    async Task ReadForeignKeysAsync(SqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT s.name, t.name,
                   STRING_AGG(pc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id),
                   rs.name + '.' + rt.name,
                   STRING_AGG(rc.name, ',') WITHIN GROUP (ORDER BY fkc.constraint_column_id)
            FROM sys.foreign_keys fk
            JOIN sys.tables t   ON t.object_id = fk.parent_object_id
            JOIN sys.schemas s  ON s.schema_id = t.schema_id
            JOIN sys.tables rt  ON rt.object_id = fk.referenced_object_id
            JOIN sys.schemas rs ON rs.schema_id = rt.schema_id
            JOIN sys.foreign_key_columns fkc ON fkc.constraint_object_id = fk.object_id
            JOIN sys.columns pc ON pc.object_id = fk.parent_object_id     AND pc.column_id = fkc.parent_column_id
            JOIN sys.columns rc ON rc.object_id = fk.referenced_object_id AND rc.column_id = fkc.referenced_column_id
            GROUP BY s.name, t.name, fk.name, rs.name, rt.name
            """;

        await using var cmd = new SqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;

            table.ForeignKeys.Add(new ForeignKeySchema
            {
                Columns = [.. reader.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries)],
                ReferencedTable = reader.GetString(3),
                ReferencedColumns = [.. reader.GetString(4).Split(',', StringSplitOptions.RemoveEmptyEntries)],
            });
        }
    }

    // --- reads -----------------------------------------------------------------

    public Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken) =>
        ExecuteReadAsync($"SELECT * FROM {QuoteQualified(table)}", new ReadOptions(rows, _statementTimeout), cancellationToken);

    public async Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken)
    {
        // cap + 1, so "exactly full" stays distinguishable from "clipped".
        var guarded = SqlServerReadGuard.Prepare(statement, options.MaxRows + 1);

        var columns = new List<ColumnInfo>();
        var rows = new JsonArray();
        var truncated = false;

        try
        {
            await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new SqlCommand(guarded.Statement, conn)
            {
                CommandTimeout = (int)Math.Ceiling(options.StatementTimeout.TotalSeconds) + 1,
            };

            await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken)
                .ConfigureAwait(false);

            for (var i = 0; i < reader.FieldCount; i++)
                columns.Add(new ColumnInfo(reader.GetName(i), reader.GetDataTypeName(i)));

            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (rows.Count >= options.MaxRows) { truncated = true; break; }

                var row = new JsonArray();
                for (var i = 0; i < reader.FieldCount; i++) Append(row, ToJson(reader, i));
                Append(rows, row);
            }
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        return new ReadResult(columns, rows, rows.Count, truncated);
    }

    public async Task<string> ExplainAsync(string statement, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var guarded = SqlServerReadGuard.Prepare(statement, maxRows: null);

        try
        {
            await using var conn = await OpenAsync(cancellationToken).ConfigureAwait(false);

            // SHOWPLAN_ALL returns the plan without executing, and is far smaller
            // than the XML form — which matters because an agent pays for it.
            await using (var on = new SqlCommand("SET SHOWPLAN_ALL ON", conn))
                await on.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var plan = new StringBuilder();
            await using (var cmd = new SqlCommand(guarded.Statement, conn))
            {
                cmd.CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) + 1;
                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (!reader.IsDBNull(0)) plan.AppendLine(reader.GetString(0));
                }
            }

            return plan.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    static JsonNode? ToJson(SqlDataReader reader, int i)
    {
        if (reader.IsDBNull(i)) return null;

        var type = reader.GetFieldType(i);
        if (type == typeof(bool)) return JsonValue.Create(reader.GetBoolean(i));
        if (type == typeof(byte)) return JsonValue.Create(reader.GetByte(i));
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

        return JsonValue.Create(reader.GetValue(i).ToString());
    }

    static string QuoteQualified(string identifier)
    {
        var sb = new StringBuilder();
        foreach (var part in identifier.Split('.'))
        {
            if (sb.Length > 0) sb.Append('.');
            sb.Append('[').Append(part.Replace("]", "]]", StringComparison.Ordinal)).Append(']');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Same distinction as every other adapter: "my statement was wrong" must not
    /// collapse into "the source is not answering".
    /// </summary>
    static AdapterException Translate(Exception ex) => ex switch
    {
        AdapterException adapter => adapter,

        // -2 is the client timeout; 1222 is lock request timeout.
        SqlException { Number: -2 or 1222 } sql
            => new AdapterException(ErrorCodes.SourceTimeout, "statement timed out", sql),

        // 53/40613/4060/18456 are unreachable, unavailable, or cannot-open-database.
        SqlException { Number: 53 or 4060 or 18456 or 40613 or 10060 or 10061 } sql
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unavailable: {sql.Message}", sql),

        SqlException sql
            => new AdapterException(ErrorCodes.SourceRejected, $"{sql.Message} (error {sql.Number})", sql),

        OperationCanceledException
            => new AdapterException(ErrorCodes.SourceTimeout, "the request was cancelled before the source answered", ex),

        _ => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {ex.Message}", ex),
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
