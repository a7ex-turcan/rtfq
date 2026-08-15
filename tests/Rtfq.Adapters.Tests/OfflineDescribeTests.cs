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

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
