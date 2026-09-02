using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Npgsql;
using Rtfq.Client;
using Rtfq.Contracts;
using Rtfq.Server;
using Rtfq.Server.Configuration;
using Testcontainers.PostgreSql;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// Discovery must survive an unreachable source.
///
/// This is the property `docs/PHASES.md` calls the single most useful one in
/// practice: an agent should be able to learn a table's shape and draft a correct
/// statement while the database is down, and only need it live to run. It gets
/// its own container because it stops it, which no shared fixture could tolerate.
/// </summary>
public sealed class OfflineDescribeTests : IAsyncLifetime
{
    const string Token = "offline-token-0123456789";

    PostgreSqlContainer _postgres = null!;
    RtfqServer _server = null!;
    string _workDir = null!;
    string _address = "";

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-offline", Guid.NewGuid().ToString("n")[..8]);
        var stateDir = Path.Combine(_workDir, "state");
        Directory.CreateDirectory(stateDir);

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("orders").Build();
        await _postgres.StartAsync();

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand(
                "CREATE TABLE orders (id int primary key, customer text not null, total numeric(10,2))", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var (certPath, keyPath) = WriteCertificate(_workDir);

        var config = new RtfqConfig
        {
            Server = new ServerSection
            {
                Listen = "127.0.0.1:0",
                Tls = new TlsSection { CertPath = certPath, KeyPath = keyPath },
                Auth = new AuthSection
                {
                    Mode = "token",
                    Tokens =
                    [
                        new TokenSection
                        {
                            Id = "offline",
                            Secret = Token,
                            SecretWasReference = true,
                            Grants = new Dictionary<string, AccessLevel> { ["orders"] = AccessLevel.Read },
                        },
                    ],
                },
            },
            Defaults = new DefaultsSection
            {
                // Short TTL so the snapshot is stale by the time the source is gone,
                // which is the harder and more realistic case.
                SchemaCacheTtl = TimeSpan.FromSeconds(1),
                StatementTimeout = TimeSpan.FromSeconds(5),
            },
            Sources =
            [
                new SourceSection
                {
                    Name = "orders",
                    Kind = "postgres",
                    Dsn = _postgres.GetConnectionString(),
                    DsnWasReference = true,
                    Access = AccessLevel.Read,
                    Schemas = ["public"],
                },
            ],
        };

        _server = await RtfqServer.StartAsync(config, stateDir);
        _address = _server.BaseAddress;
    }

    static (string Cert, string Key) WriteCertificate(string directory)
    {
        Directory.CreateDirectory(directory);
        var certPath = Path.Combine(directory, "tls.crt");
        var keyPath = Path.Combine(directory, "tls.key");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(System.Net.IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    RtfqClient Client() => new(_address, Token, skipCertificateValidation: true);

    async Task CreateTable(string name)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"CREATE TABLE {name} (id int primary key, note text)", conn);
        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task Describe_keeps_answering_after_the_source_goes_down()
    {
        using var client = Client();

        // Warm the cache while the database is up.
        var before = await client.DescribeTableAsync("orders", "public.orders");
        Assert.Equal(3, before.Columns.Count);
        Assert.False(before.Schema.Stale);

        // Now take the database away.
        await _postgres.StopAsync();

        // Discovery still answers, from cache, and says how old the answer is.
        var afterSource = await client.DescribeSourceAsync("orders");
        Assert.Single(afterSource.Tables);
        Assert.Equal("public.orders", afterSource.Tables[0].Name);

        var afterTable = await client.DescribeTableAsync("orders", "public.orders");
        Assert.Equal(3, afterTable.Columns.Count);
        Assert.Contains(afterTable.Columns, c => c.Name == "customer" && c.Type == "text");

        // An agent can draft a correct statement offline; it just cannot run one.
        //
        // Stopping a container leaves a window where PostgreSQL still answers, with
        // a FATAL shutdown error rather than a refused connection. Both are the
        // source being unavailable, and both must classify that way — telling the
        // caller its statement was rejected would send it debugging valid SQL.
        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.QueryAsync("orders", "SELECT id FROM orders"));

        Assert.Equal(ErrorCodes.SourceUnreachable, ex.Code);
    }

    [Fact]
    public async Task A_stale_snapshot_reports_its_age_rather_than_pretending()
    {
        using var client = Client();

        await client.DescribeSourceAsync("orders");
        await _postgres.StopAsync();

        // Past the one-second TTL, so this is the stale path with no way to refresh.
        await Task.Delay(TimeSpan.FromSeconds(2));

        var result = await client.DescribeSourceAsync("orders");

        Assert.True(result.Schema.Stale, "a snapshot past its TTL must say so");
        Assert.True(result.Schema.AgeSeconds >= 1);
        Assert.Single(result.Tables);
    }

    // --- a stale cache must not turn a new table into a phantom missing one ----
    //
    // The field report: describe_table for a table created after the cached
    // snapshot answered "no table X in source" from a 12-day-old cache, so an
    // agent verifying a migration concluded it had not run. It had.

    [Fact]
    public async Task A_table_created_after_the_snapshot_is_found_rather_than_reported_missing()
    {
        using var client = Client();

        // Warm the cache. It now knows 'orders' and not the table we add next.
        await client.DescribeTableAsync("orders", "public.orders");

        // A migration adds a table after that snapshot. The TTL is one second, so
        // by the time we ask, the cache is stale - the reported shape exactly.
        await CreateTable("rule_condition");
        await Task.Delay(TimeSpan.FromSeconds(2));

        // describe_table must confirm against the live source, not answer "absent"
        // from a snapshot that predates the table.
        var result = await client.DescribeTableAsync("orders", "public.rule_condition");

        Assert.Equal("public.rule_condition", result.Table);
        Assert.False(result.Schema.Stale, "the confirming re-read makes the served snapshot fresh");
    }

    [Fact]
    public async Task A_genuinely_missing_table_is_a_table_code_not_a_source_code()
    {
        using var client = Client();
        await client.DescribeTableAsync("orders", "public.orders");
        await Task.Delay(TimeSpan.FromSeconds(2));    // stale, so the miss re-reads live

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.DescribeTableAsync("orders", "public.nope"));

        // policy.source_unknown would say the source is gone or forbidden; it is
        // neither, and an agent must be able to tell those apart.
        Assert.Equal(ErrorCodes.TableUnknown, ex.Code);
        Assert.DoesNotContain("source_unknown", ex.Code);

        // Having re-read the live source, the answer is current, not a hedge.
        Assert.Contains("current", ex.Message);
    }

    [Fact]
    public async Task A_missing_table_on_an_unreachable_source_says_it_may_exist_rather_than_asserting_absence()
    {
        using var client = Client();
        await client.DescribeTableAsync("orders", "public.orders");

        await _postgres.StopAsync();
        await Task.Delay(TimeSpan.FromSeconds(2));    // stale, and now unconfirmable

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.DescribeTableAsync("orders", "public.nope"));

        Assert.Equal(ErrorCodes.TableUnknown, ex.Code);

        // The honest answer when the source could not be checked: it might be there.
        Assert.Contains("may exist", ex.Message);
        Assert.Contains("could not be reached", ex.Message);
    }

    [Fact]
    public async Task Refresh_makes_a_newly_created_table_visible_at_once()
    {
        using var client = Client();
        await client.DescribeSourceAsync("orders");

        await CreateTable("added_by_migration");

        var refreshed = await client.RefreshAsync("orders");
        Assert.False(refreshed.Schema.Stale);

        var listed = await client.DescribeSourceAsync("orders", pattern: "added_by_migration");
        Assert.Contains(listed.Tables, t => t.Name == "public.added_by_migration");
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
