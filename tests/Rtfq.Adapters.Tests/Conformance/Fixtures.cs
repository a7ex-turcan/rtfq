using System.Net;
using System.Text;
using Microsoft.Data.SqlClient;
using MongoDB.Bson;
using MongoDB.Driver;
using Npgsql;
using Rtfq.Adapters.Http;
using Rtfq.Adapters.Mongo;
using Rtfq.Adapters.Postgres;
using Rtfq.Adapters.SqlServer;
using Testcontainers.MongoDb;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace Rtfq.Adapters.Tests.Conformance;

// Each fixture supplies the same facts in a different dialect. That the suite
// itself needs no per-adapter branching is the M2 exit criterion.

public sealed class PostgresConformanceFixture : IAdapterFixture, IAsyncLifetime
{
    PostgreSqlContainer _container = null!;
    PostgresAdapter _adapter = null!;

    public ISourceAdapter Adapter => _adapter;
    public string SampleTarget => "public.widgets";
    public string ReadStatement => "SELECT id, name FROM widgets ORDER BY id";
    public string WriteStatement => "UPDATE widgets SET name = 'x' WHERE id = 1";
    public string NonsenseStatement => "@@@ not sql at all";
    public int SeededRows => 5;
    public bool SupportsExplain => true;
    public bool SupportsIntrospection => true;

    public async Task InitializeAsync()
    {
        _container = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("conf").Build();
        await _container.StartAsync();

        await using var conn = new NpgsqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE widgets (id int primary key, name text not null, weight numeric(8,2));
            INSERT INTO widgets SELECT g, 'widget-' || g, g * 1.5 FROM generate_series(1,5) g;
            ANALYZE widgets;
            """, conn);
        await cmd.ExecuteNonQueryAsync();

        _adapter = new PostgresAdapter("pg", _container.GetConnectionString(), ["public"], TimeSpan.FromSeconds(15));
    }

    public async Task DisposeAsync()
    {
        await _adapter.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class SqlServerConformanceFixture : IAdapterFixture, IAsyncLifetime
{
    MsSqlContainer _container = null!;
    SqlServerAdapter _adapter = null!;

    public ISourceAdapter Adapter => _adapter;
    public string SampleTarget => "dbo.widgets";
    public string ReadStatement => "SELECT id, name FROM widgets ORDER BY id";
    public string WriteStatement => "UPDATE widgets SET name = 'x' WHERE id = 1";
    public string NonsenseStatement => "@@@ not sql at all";
    public int SeededRows => 5;
    public bool SupportsExplain => true;
    public bool SupportsIntrospection => true;

    public async Task InitializeAsync()
    {
        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();

        await using var conn = new SqlConnection(_container.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new SqlCommand("""
            CREATE TABLE widgets (id int primary key, name nvarchar(100) not null, weight decimal(8,2) null);
            INSERT INTO widgets (id, name, weight) VALUES
              (1,'widget-1',1.5),(2,'widget-2',3.0),(3,'widget-3',4.5),(4,'widget-4',6.0),(5,'widget-5',7.5);
            """, conn);
        await cmd.ExecuteNonQueryAsync();

        _adapter = new SqlServerAdapter("mssql", _container.GetConnectionString(), ["dbo"], TimeSpan.FromSeconds(15));
    }

    public async Task DisposeAsync()
    {
        await _adapter.DisposeAsync();
        await _container.DisposeAsync();
    }
}

public sealed class MongoConformanceFixture : IAdapterFixture, IAsyncLifetime
{
    MongoDbContainer _container = null!;
    MongoAdapter _adapter = null!;

    public ISourceAdapter Adapter => _adapter;
    public string SampleTarget => "conf.widgets";

    // MongoDB's native dialect is a command document, not a string of SQL.
    public string ReadStatement => """{"find": "widgets", "sort": {"id": 1}}""";
    public string WriteStatement => """{"update": "widgets", "updates": [{"q": {"id": 1}, "u": {"$set": {"name": "x"}}}]}""";
    public string NonsenseStatement => "@@@ not a command document";
    public int SeededRows => 5;
    public bool SupportsExplain => true;
    public bool SupportsIntrospection => true;

    public async Task InitializeAsync()
    {
        _container = new MongoDbBuilder("mongo:7").Build();
        await _container.StartAsync();

        var client = new MongoClient(_container.GetConnectionString());
        var collection = client.GetDatabase("conf").GetCollection<BsonDocument>("widgets");
        await collection.InsertManyAsync(Enumerable.Range(1, 5).Select(i => new BsonDocument
        {
            ["id"] = i,
            ["name"] = $"widget-{i}",
            ["weight"] = i * 1.5,
        }));

        _adapter = new MongoAdapter("mongo", _container.GetConnectionString(), ["conf"], TimeSpan.FromSeconds(15));
    }

    public async Task DisposeAsync()
    {
        await _adapter.DisposeAsync();
        await _container.DisposeAsync();
    }
}

/// <summary>
/// The HTTP fixture serves its own API in-process. No container: the thing under
/// test is the adapter's allow-list and flattening, and a real third-party API
/// would add network flakiness without adding coverage.
/// </summary>
public sealed class HttpConformanceFixture : IAdapterFixture, IAsyncLifetime
{
    HttpListener _listener = null!;
    HttpAdapter _adapter = null!;
    CancellationTokenSource _stopping = null!;

    public ISourceAdapter Adapter => _adapter;
    public string SampleTarget => "/widgets";
    public string ReadStatement => "GET /widgets";
    public string WriteStatement => "POST /widgets";
    public string NonsenseStatement => "not-a-request-line";
    public int SeededRows => 5;
    public bool SupportsExplain => false;         // an HTTP API has no query plan
    public bool SupportsIntrospection => true;    // the allow-list is the "schema"

    public Task InitializeAsync()
    {
        var port = FreePort();
        var prefix = $"http://127.0.0.1:{port}/";

        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _listener.Start();
        _stopping = new CancellationTokenSource();
        _ = Task.Run(() => ServeAsync(_stopping.Token));

        _adapter = new HttpAdapter(
            "api", prefix,
            methods: ["GET"],
            allowPaths: ["/widgets", "/widgets/*"],
            headers: new Dictionary<string, string> { ["X-Api-Key"] = "secret" },
            timeout: TimeSpan.FromSeconds(15));

        return Task.CompletedTask;
    }

    async Task ServeAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }   // listener stopped

            var body = context.Request.Url?.AbsolutePath switch
            {
                "/widgets" => """
                    [{"id":1,"name":"widget-1"},{"id":2,"name":"widget-2"},{"id":3,"name":"widget-3"},
                     {"id":4,"name":"widget-4"},{"id":5,"name":"widget-5"}]
                    """,
                _ => """{"error":"not found"}""",
            };

            context.Response.StatusCode = context.Request.Url?.AbsolutePath == "/widgets" ? 200 : 404;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await context.Response.OutputStream.WriteAsync(bytes, cancellationToken);
            context.Response.Close();
        }
    }

    static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task DisposeAsync()
    {
        await _stopping.CancelAsync();
        _listener.Stop();
        _listener.Close();
        await _adapter.DisposeAsync();
    }
}

// --- the four runs of the same suite ---------------------------------------

public sealed class PostgresConformanceTests(PostgresConformanceFixture fixture)
    : AdapterConformance<PostgresConformanceFixture>(fixture), IClassFixture<PostgresConformanceFixture>;

public sealed class SqlServerConformanceTests(SqlServerConformanceFixture fixture)
    : AdapterConformance<SqlServerConformanceFixture>(fixture), IClassFixture<SqlServerConformanceFixture>;

public sealed class MongoConformanceTests(MongoConformanceFixture fixture)
    : AdapterConformance<MongoConformanceFixture>(fixture), IClassFixture<MongoConformanceFixture>;

public sealed class HttpConformanceTests(HttpConformanceFixture fixture)
    : AdapterConformance<HttpConformanceFixture>(fixture), IClassFixture<HttpConformanceFixture>;
