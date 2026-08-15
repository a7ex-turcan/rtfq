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
    readonly string[] _schemas;

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
        _schemas = [.. schemas];

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

        // The slim builder opts out of array support by default, and introspection
        // reads array_agg results and passes schema lists as parameters.
        _dataSource = new NpgsqlSlimDataSourceBuilder(csb.ConnectionString)
            .EnableArrays()
            .Build();
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
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

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

    // Introspection reads pg_catalog directly rather than information_schema: the
    // catalog carries planner row estimates and index definitions that the
    // standard views do not expose.

    async Task<Dictionary<string, TableSchema>> ReadTablesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname,
                   c.relname,
                   CASE c.relkind
                       WHEN 'r' THEN 'table' WHEN 'p' THEN 'table'
                       WHEN 'v' THEN 'view'  WHEN 'm' THEN 'matview'
                       ELSE 'foreign' END,
                   CASE WHEN c.reltuples < 0 THEN NULL ELSE c.reltuples::bigint END
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.relkind = ANY (ARRAY['r','p','v','m','f'])
              AND n.nspname <> ALL (@excluded)
              AND (cardinality(@schemas) = 0 OR n.nspname = ANY (@schemas))
            """;

        var tables = new Dictionary<string, TableSchema>(StringComparer.Ordinal);

        await using var cmd = new NpgsqlCommand(sql, conn);
        AddSchemaParameters(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var table = new TableSchema
            {
                Schema = reader.GetString(0),
                Name = reader.GetString(1),
                Kind = reader.GetString(2),
                EstimatedRows = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Columns = [],
            };
            tables[table.QualifiedName] = table;
        }

        return tables;
    }

    async Task ReadColumnsAsync(NpgsqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, a.attname,
                   format_type(a.atttypid, a.atttypmod),
                   NOT a.attnotnull,
                   pg_get_expr(d.adbin, d.adrelid)
            FROM pg_attribute a
            JOIN pg_class c ON c.oid = a.attrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            LEFT JOIN pg_attrdef d ON d.adrelid = a.attrelid AND d.adnum = a.attnum
            WHERE a.attnum > 0 AND NOT a.attisdropped
              AND c.relkind = ANY (ARRAY['r','p','v','m','f'])
              AND n.nspname <> ALL (@excluded)
              AND (cardinality(@schemas) = 0 OR n.nspname = ANY (@schemas))
            ORDER BY n.nspname, c.relname, a.attnum
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        AddSchemaParameters(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;
            table.Columns.Add(new ColumnSchema
            {
                Name = reader.GetString(2),
                Type = reader.GetString(3),
                Nullable = reader.GetBoolean(4),
                Default = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }
    }

    async Task ReadIndexesAsync(NpgsqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname, i.relname, ix.indisunique, ix.indisprimary,
                   array_agg(a.attname ORDER BY k.ord)
            FROM pg_index ix
            JOIN pg_class c ON c.oid = ix.indrelid
            JOIN pg_class i ON i.oid = ix.indexrelid
            JOIN pg_namespace n ON n.oid = c.relnamespace
            JOIN LATERAL unnest(ix.indkey) WITH ORDINALITY AS k(attnum, ord) ON true
            JOIN pg_attribute a ON a.attrelid = c.oid AND a.attnum = k.attnum
            WHERE n.nspname <> ALL (@excluded)
              AND (cardinality(@schemas) = 0 OR n.nspname = ANY (@schemas))
            GROUP BY n.nspname, c.relname, i.relname, ix.indisunique, ix.indisprimary
            ORDER BY n.nspname, c.relname, i.relname
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        AddSchemaParameters(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;

            var columns = reader.GetFieldValue<string[]>(5).ToList();
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

    async Task ReadForeignKeysAsync(NpgsqlConnection conn, Dictionary<string, TableSchema> tables, CancellationToken ct)
    {
        const string sql = """
            SELECT n.nspname, c.relname,
                   array_agg(att.attname  ORDER BY k.ord),
                   fn.nspname || '.' || fc.relname,
                   array_agg(fatt.attname ORDER BY k.ord)
            FROM pg_constraint con
            JOIN pg_class c       ON c.oid  = con.conrelid
            JOIN pg_namespace n   ON n.oid  = c.relnamespace
            JOIN pg_class fc      ON fc.oid = con.confrelid
            JOIN pg_namespace fn  ON fn.oid = fc.relnamespace
            JOIN LATERAL unnest(con.conkey, con.confkey) WITH ORDINALITY AS k(att, fatt, ord) ON true
            JOIN pg_attribute att  ON att.attrelid  = con.conrelid  AND att.attnum  = k.att
            JOIN pg_attribute fatt ON fatt.attrelid = con.confrelid AND fatt.attnum = k.fatt
            WHERE con.contype = 'f'
              AND n.nspname <> ALL (@excluded)
              AND (cardinality(@schemas) = 0 OR n.nspname = ANY (@schemas))
            GROUP BY n.nspname, c.relname, con.conname, fn.nspname, fc.relname
            ORDER BY n.nspname, c.relname, con.conname
            """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        AddSchemaParameters(cmd);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);

        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            if (!tables.TryGetValue($"{reader.GetString(0)}.{reader.GetString(1)}", out var table)) continue;
            table.ForeignKeys.Add(new ForeignKeySchema
            {
                Columns = reader.GetFieldValue<string[]>(2).ToList(),
                ReferencedTable = reader.GetString(3),
                ReferencedColumns = reader.GetFieldValue<string[]>(4).ToList(),
            });
        }
    }

    void AddSchemaParameters(NpgsqlCommand cmd)
    {
        cmd.Parameters.AddWithValue("schemas", _schemas);
        cmd.Parameters.AddWithValue("excluded", SystemSchemas);
    }

    static readonly string[] SystemSchemas = ["pg_catalog", "information_schema", "pg_toast"];

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
        // Guard first, execute second. Until M1 this method ran whatever it was
        // given, which meant a read-granted token could send an UPDATE and only
        // the database GRANT stood in the way.
        //
        // The injected limit is cap + 1, not cap. Asking for exactly the cap makes
        // "the table had exactly 100 rows" indistinguishable from "there were more
        // and we stopped", and reporting the second as the first is the silent
        // truncation this envelope exists to prevent. The extra row is read and
        // discarded purely as evidence.
        var guarded = PostgresReadGuard.Prepare(statement, options.MaxRows + 1);

        var columns = new List<ColumnInfo>();
        var rows = new JsonArray();
        var truncated = false;

        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var cmd = new NpgsqlCommand(guarded.Statement, conn)
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

    public async Task<string> ExplainAsync(string statement, TimeSpan timeout, CancellationToken cancellationToken)
    {
        // Validate without injecting: a LIMIT the caller did not write would
        // change the plan they asked to see.
        var guarded = PostgresReadGuard.Prepare(statement, maxRows: null);

        var plan = new StringBuilder();
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

            // FORMAT TEXT, not JSON: this output is read by an agent paying for
            // every token, and the JSON form of a plan is several times the size
            // for the same information.
            await using var cmd = new NpgsqlCommand($"EXPLAIN (COSTS true, FORMAT TEXT) {guarded.Statement}", conn)
            {
                CommandTimeout = (int)Math.Ceiling(timeout.TotalSeconds) + 1,
            };

            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                plan.AppendLine(reader.GetString(0));
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }

        return plan.ToString().TrimEnd();
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

        // The engine can answer and still be unavailable: a server that is shutting
        // down or not yet accepting connections replies with an error, and reporting
        // that as "your statement was rejected" sends the caller to debug a
        // statement that was fine. Class 08 is connection_exception; 57P0x is
        // admin/crash shutdown and cannot_connect_now.
        PostgresException pg when pg.SqlState.StartsWith("08", StringComparison.Ordinal)
                                  || pg.SqlState is "57P01" or "57P02" or "57P03"
            => new AdapterException(ErrorCodes.SourceUnreachable,
                $"source is unavailable: {pg.MessageText} (SQLSTATE {pg.SqlState})", pg),

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
