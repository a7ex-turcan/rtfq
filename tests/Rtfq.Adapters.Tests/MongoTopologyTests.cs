using MongoDB.Bson;
using MongoDB.Driver;
using Rtfq.Adapters.Mongo;
using Rtfq.Contracts;
using Rtfq.Server;
using Rtfq.Server.Configuration;
using Testcontainers.MongoDb;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// The check that offline validation cannot make.
///
/// MongoDB does transactions on a replica set and not on a standalone, and no
/// amount of reading YAML reveals which is out there. CLAUDE.md says a source
/// whose adapter cannot do transactional writes may not be marked writable — so
/// enforcing it means connecting, which is why <c>rtfq validate</c> stays static
/// and the server checks at startup.
/// </summary>
public sealed class MongoTopologyTests : IAsyncLifetime
{
    MongoDbContainer _standalone = null!;

    public async Task InitializeAsync()
    {
        _standalone = new MongoDbBuilder("mongo:7").Build();
        await _standalone.StartAsync();

        var client = new MongoClient(_standalone.GetConnectionString());
        await client.GetDatabase("shop").GetCollection<BsonDocument>("orders")
            .InsertOneAsync(new BsonDocument { ["id"] = 1 });
    }

    RtfqConfig ConfigWithAccess(AccessLevel access) => new()
    {
        Server = new ServerSection
        {
            Listen = "127.0.0.1:0",
            Auth = new AuthSection
            {
                Mode = "token",
                Tokens =
                [
                    new TokenSection
                    {
                        Id = "agent", Secret = "s", SecretWasReference = true,
                        Grants = new Dictionary<string, AccessLevel> { ["shop"] = access },
                    },
                ],
            },
        },
        Defaults = new DefaultsSection(),
        Sources =
        [
            new SourceSection
            {
                Name = "shop",
                Kind = "mongodb",
                Dsn = _standalone.GetConnectionString(),
                DsnWasReference = true,
                Access = access,
                Databases = ["shop"],
            },
        ],
    };

    [Fact]
    public async Task A_standalone_reports_that_it_cannot_do_transactional_writes()
    {
        await using var adapter = new MongoAdapter(
            "shop", _standalone.GetConnectionString(), ["shop"], TimeSpan.FromSeconds(15));

        var capabilities = await adapter.CheckAsync(CancellationToken.None);

        Assert.False(capabilities.TransactionalWrites, "a standalone mongod has no transactions");
        Assert.False(capabilities.TransactionalDdl, "mongo cannot roll back createIndex under any topology");
        Assert.True(capabilities.Introspection);
    }

    [Fact]
    public async Task Declaring_write_on_a_standalone_is_fatal_at_startup()
    {
        var config = ConfigWithAccess(AccessLevel.Write);
        await using var registry = new SourceRegistry(config);

        var problems = await registry.CheckCapabilitiesAsync(config, TimeSpan.FromSeconds(15), CancellationToken.None);

        var problem = Assert.Single(problems);
        Assert.True(problem.Fatal, "a source that cannot support its declared access must stop startup");
        Assert.Contains("replica set", problem.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declaring_read_on_a_standalone_is_fine()
    {
        var config = ConfigWithAccess(AccessLevel.Read);
        await using var registry = new SourceRegistry(config);

        var problems = await registry.CheckCapabilitiesAsync(config, TimeSpan.FromSeconds(15), CancellationToken.None);

        Assert.Empty(problems);
    }

    /// <summary>
    /// A source being down must NOT stop the server: refusing to start because one
    /// database is unreachable would contradict the offline-discovery posture the
    /// whole schema cache exists to provide.
    /// </summary>
    [Fact]
    public async Task An_unreachable_source_is_a_warning_not_a_failure()
    {
        var config = ConfigWithAccess(AccessLevel.Read);
        config = config with
        {
            Sources = [config.Sources[0] with { Dsn = "mongodb://127.0.0.1:1/?serverSelectionTimeoutMS=500" }],
        };

        await using var registry = new SourceRegistry(config);
        var problems = await registry.CheckCapabilitiesAsync(config, TimeSpan.FromSeconds(5), CancellationToken.None);

        var problem = Assert.Single(problems);
        Assert.False(problem.Fatal);
        Assert.Contains("could not be reached", problem.Message, StringComparison.Ordinal);
    }

    public async Task DisposeAsync() => await _standalone.DisposeAsync();
}
