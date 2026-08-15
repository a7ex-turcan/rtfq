using System.Text.Json.Nodes;
using MongoDB.Bson;
using MongoDB.Driver;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Mongo;

/// <summary>
/// MongoDB via the official driver, used through <see cref="BsonDocument"/> only.
///
/// Never POCO mapping: that path is the driver's reflective machinery, which does
/// not survive trimming, and avoiding it is what keeps the published binary
/// honest. See the note in Directory.Build.props.
///
/// Two things make this adapter the interesting one. Its schema is <b>inferred</b>
/// from sampled documents rather than read from a catalog, and it must say so —
/// an agent told "these are the columns" about a schemaless store has been
/// misled. And its transaction support depends on the deployment topology, which
/// is why <see cref="CheckAsync"/> returns capabilities.
/// </summary>
public sealed class MongoAdapter : ISourceAdapter
{
    /// <summary>Documents sampled per collection when inferring shape.</summary>
    public const int InferenceSampleSize = 50;

    static readonly string[] SystemDatabases = ["admin", "config", "local"];

    readonly MongoClient _client;
    readonly string[] _databases;
    readonly TimeSpan _statementTimeout;

    public string Name { get; }
    public string Kind => "mongodb";

    /// <summary>
    /// Declared pessimistically. A standalone MongoDB cannot do transactions at
    /// all, and we do not know which we have until we connect, so the safe
    /// assumption is the restrictive one.
    /// </summary>
    public SourceCapabilities Capabilities { get; private set; } = new(
        TransactionalWrites: false,
        TransactionalDdl: false,
        Explain: true,
        Introspection: true);

    public MongoAdapter(string name, string uri, IReadOnlyList<string> databases, TimeSpan statementTimeout)
    {
        Name = name;
        _databases = [.. databases];
        _statementTimeout = statementTimeout;

        try
        {
            var settings = MongoClientSettings.FromConnectionString(uri);
            settings.ServerSelectionTimeout = statementTimeout;
            settings.ConnectTimeout = statementTimeout;
            _client = new MongoClient(settings);
        }
        catch (Exception ex) when (ex is MongoConfigurationException or FormatException)
        {
            throw new AdapterException(ErrorCodes.ConfigInvalid, $"source '{name}' has an unparseable uri: {ex.Message}", ex);
        }
    }

    public async Task<SourceCapabilities> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            var admin = _client.GetDatabase("admin");
            var hello = await admin.RunCommandAsync<BsonDocument>(
                new BsonDocument("hello", 1), cancellationToken: cancellationToken).ConfigureAwait(false);

            // Transactions require a replica set or a sharded cluster. A standalone
            // reports neither setName nor the mongos marker, and per CLAUDE.md a
            // source whose adapter cannot do transactional writes may not be marked
            // writable — so this is the fact that decides it.
            var replicaSet = hello.Contains("setName");
            var sharded = hello.TryGetValue("msg", out var msg) && msg.AsString == "isdbgrid";
            var transactional = replicaSet || sharded;

            Capabilities = Capabilities with
            {
                TransactionalWrites = transactional,
                // Mongo cannot roll back createIndex or dropCollection under any
                // topology, so access: schema is never available here (ADR 0002).
                TransactionalDdl = false,
            };

            return Capabilities;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    // --- introspection ------------------------------------------------------

    public async Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var tables = new List<TableSchema>();

            foreach (var databaseName in await DatabasesAsync(cancellationToken).ConfigureAwait(false))
            {
                var database = _client.GetDatabase(databaseName);
                var names = await (await database.ListCollectionNamesAsync(cancellationToken: cancellationToken)
                    .ConfigureAwait(false)).ToListAsync(cancellationToken).ConfigureAwait(false);

                foreach (var collectionName in names.Where(n => !n.StartsWith("system.", StringComparison.Ordinal)))
                {
                    tables.Add(await InferCollectionAsync(database, databaseName, collectionName, cancellationToken)
                        .ConfigureAwait(false));
                }
            }

            return new SchemaSnapshot
            {
                Source = Name,
                CapturedAt = DateTimeOffset.UtcNow,
                // The whole point: this schema was guessed from data, and a caller
                // that does not know that will trust it too far.
                Inferred = true,
                Tables = [.. tables.OrderBy(t => t.Schema, StringComparer.Ordinal)
                                   .ThenBy(t => t.Name, StringComparer.Ordinal)],
            };
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    async Task<List<string>> DatabasesAsync(CancellationToken cancellationToken)
    {
        if (_databases.Length > 0) return [.. _databases];

        var all = await (await _client.ListDatabaseNamesAsync(cancellationToken).ConfigureAwait(false))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return [.. all.Where(n => !SystemDatabases.Contains(n, StringComparer.Ordinal))];
    }

    /// <summary>
    /// Infers a collection's shape from a sample. Fields are reported with every
    /// type observed, because a field that is a string in some documents and an
    /// int in others is a fact an agent needs before writing a predicate — and
    /// collapsing it to one type would be a lie.
    /// </summary>
    static async Task<TableSchema> InferCollectionAsync(
        IMongoDatabase database, string databaseName, string collectionName, CancellationToken cancellationToken)
    {
        var collection = database.GetCollection<BsonDocument>(collectionName);

        var estimated = await collection.EstimatedDocumentCountAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var sample = await collection.Find(new BsonDocument())
            .Limit(InferenceSampleSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var types = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var seenIn = new Dictionary<string, int>(StringComparer.Ordinal);
        var order = new List<string>();

        foreach (var document in sample)
        {
            foreach (var element in document)
            {
                if (!types.TryGetValue(element.Name, out var observed))
                {
                    observed = new SortedSet<string>(StringComparer.Ordinal);
                    types[element.Name] = observed;
                    order.Add(element.Name);
                }
                observed.Add(TypeName(element.Value));
                seenIn[element.Name] = seenIn.GetValueOrDefault(element.Name) + 1;
            }
        }

        var columns = order.Select(field => new ColumnSchema
        {
            Name = field,
            Type = string.Join('|', types[field]),
            // "Nullable" here means "absent from some sampled documents", which is
            // the closest honest analogue in a schemaless store.
            Nullable = seenIn[field] < sample.Count,
        }).ToList();

        var indexes = new List<IndexSchema>();
        var primaryKey = new List<string>();
        try
        {
            var raw = await (await collection.Indexes.ListAsync(cancellationToken).ConfigureAwait(false))
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            foreach (var index in raw)
            {
                var keys = index["key"].AsBsonDocument.Names.ToList();
                var name = index.TryGetValue("name", out var n) ? n.AsString : string.Join('_', keys);
                var unique = index.TryGetValue("unique", out var u) && u.ToBoolean();
                var primary = name == "_id_";

                indexes.Add(new IndexSchema { Name = name, Columns = keys, Unique = unique || primary, Primary = primary });
                if (primary) primaryKey.AddRange(keys);
            }
        }
        catch (MongoException)
        {
            // Index listing can be denied independently of read access. Losing it
            // is worth less than losing the whole description.
        }

        return new TableSchema
        {
            Schema = databaseName,
            Name = collectionName,
            Kind = "collection",
            EstimatedRows = estimated,
            Columns = columns,
            PrimaryKey = primaryKey,
            Indexes = indexes,
        };
    }

    static string TypeName(BsonValue value) => value.BsonType switch
    {
        BsonType.Double => "double",
        BsonType.String => "string",
        BsonType.Document => "object",
        BsonType.Array => "array",
        BsonType.Binary => "binary",
        BsonType.ObjectId => "objectId",
        BsonType.Boolean => "bool",
        BsonType.DateTime => "date",
        BsonType.Null => "null",
        BsonType.Int32 => "int32",
        BsonType.Int64 => "int64",
        BsonType.Decimal128 => "decimal",
        BsonType.RegularExpression => "regex",
        BsonType.Timestamp => "timestamp",
        _ => value.BsonType.ToString().ToLowerInvariant(),
    };

    // --- reads -----------------------------------------------------------------

    public Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken)
    {
        var (database, collection) = Split(table);
        var command = new BsonDocument { ["find"] = collection, ["limit"] = rows };
        return RunAsync(database, command, rows, cancellationToken);
    }

    public Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken)
    {
        // cap + 1, so "exactly full" stays distinguishable from "clipped".
        var guarded = MongoReadGuard.Prepare(statement, options.MaxRows + 1);
        var database = DefaultDatabase();
        return RunAsync(database, guarded.Command, options.MaxRows, cancellationToken);
    }

    async Task<ReadResult> RunAsync(string databaseName, BsonDocument command, int maxRows, CancellationToken cancellationToken)
    {
        try
        {
            var database = _client.GetDatabase(databaseName);
            var response = await database.RunCommandAsync<BsonDocument>(command, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var documents = ExtractDocuments(response);
            return Tabulate(documents, maxRows);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// find and aggregate return a cursor envelope; count and distinct return a
    /// scalar. Both are normalised to a list of documents so the tabulation below
    /// does not care which command ran.
    /// </summary>
    static List<BsonDocument> ExtractDocuments(BsonDocument response)
    {
        if (response.TryGetValue("cursor", out var cursor) && cursor is BsonDocument cursorDocument)
        {
            var batch = cursorDocument.GetValue("firstBatch", new BsonArray());
            return [.. batch.AsBsonArray.OfType<BsonDocument>()];
        }

        if (response.TryGetValue("n", out var n))
            return [new BsonDocument("n", n)];

        if (response.TryGetValue("values", out var values) && values is BsonArray array)
            return [.. array.Select(v => new BsonDocument("value", v))];

        return [];
    }

    /// <summary>
    /// Flattens documents into the columnar envelope every source shares.
    ///
    /// The impedance is real — documents are not rows — and it is resolved HERE,
    /// in the adapter, rather than by giving the core a second response shape.
    /// Columns are the union of top-level fields in first-seen order; a document
    /// missing one gets a null; nested documents and arrays render as JSON.
    /// </summary>
    static ReadResult Tabulate(List<BsonDocument> documents, int maxRows)
    {
        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var document in documents.Take(maxRows))
        {
            foreach (var element in document)
                if (seen.Add(element.Name)) order.Add(element.Name);
        }

        var rows = new JsonArray();
        var truncated = false;

        foreach (var document in documents)
        {
            if (rows.Count >= maxRows) { truncated = true; break; }

            var row = new JsonArray();
            foreach (var field in order)
                Append(row, document.TryGetValue(field, out var value) ? ToJson(value) : null);
            Append(rows, row);
        }

        var columns = order.Select(f => new ColumnInfo(f, "bson")).ToList();
        return new ReadResult(columns, rows, rows.Count, truncated);
    }

    public async Task<string> ExplainAsync(string statement, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var guarded = MongoReadGuard.Prepare(statement, maxRows: null);

        try
        {
            var database = _client.GetDatabase(DefaultDatabase());
            var explain = new BsonDocument
            {
                ["explain"] = guarded.Command,
                ["verbosity"] = "queryPlanner",   // never executionStats: that runs the query
            };

            var response = await database.RunCommandAsync<BsonDocument>(explain, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var plan = response.TryGetValue("queryPlanner", out var queryPlanner) ? queryPlanner : response;
            return plan.ToJson(new MongoDB.Bson.IO.JsonWriterSettings { Indent = true });
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    string DefaultDatabase() =>
        _databases.Length > 0
            ? _databases[0]
            : throw new AdapterException(ErrorCodes.ConfigInvalid,
                $"source '{Name}' must declare 'databases' so a command knows where to run");

    (string Database, string Collection) Split(string qualified)
    {
        var dot = qualified.IndexOf('.', StringComparison.Ordinal);
        return dot > 0
            ? (qualified[..dot], qualified[(dot + 1)..])
            : (DefaultDatabase(), qualified);
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    static JsonNode? ToJson(BsonValue value) => value.BsonType switch
    {
        BsonType.Null or BsonType.Undefined => null,
        BsonType.Boolean => JsonValue.Create(value.AsBoolean),
        BsonType.Int32 => JsonValue.Create(value.AsInt32),
        BsonType.Int64 => JsonValue.Create(value.AsInt64),
        BsonType.Double => JsonValue.Create(value.AsDouble),
        BsonType.Decimal128 => JsonValue.Create(value.AsDecimal),
        BsonType.String => JsonValue.Create(value.AsString),
        BsonType.ObjectId => JsonValue.Create(value.AsObjectId.ToString()),
        BsonType.DateTime => JsonValue.Create(value.ToUniversalTime().ToString("O")),
        BsonType.Binary => JsonValue.Create(Convert.ToBase64String(value.AsBsonBinaryData.Bytes)),
        // Nested structure keeps its shape as JSON rather than being flattened
        // into columns that would differ per document.
        _ => JsonValue.Create(value.ToJson()),
    };

    static AdapterException Translate(Exception ex) => ex switch
    {
        AdapterException adapter => adapter,

        MongoCommandException command
            => new AdapterException(ErrorCodes.SourceRejected, $"{command.ErrorMessage} (code {command.Code})", command),

        MongoConnectionException or MongoNotPrimaryException or TimeoutException
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {ex.Message}", ex),

        // Raised when no server can be selected within the timeout: the cluster is
        // not answering, which is unreachable rather than slow.
        MongoClientException client when client.Message.Contains("server selection", StringComparison.OrdinalIgnoreCase)
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {client.Message}", client),

        OperationCanceledException
            => new AdapterException(ErrorCodes.SourceTimeout, "the request was cancelled before the source answered", ex),

        MongoException
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {ex.Message}", ex),

        _ => new AdapterException(ErrorCodes.Internal, ex.Message, ex),
    };

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
