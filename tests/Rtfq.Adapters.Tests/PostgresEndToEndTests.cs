using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Npgsql;
using Rtfq.Client;
using Rtfq.Contracts;
using Rtfq.Server;
using Rtfq.Server.Configuration;
using Testcontainers.PostgreSql;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// A real PostgreSQL in a container, a real TLS listener, a real HTTP client.
/// No mocks: per the working agreements, an adapter tested against a fake proves
/// only that the fake behaves like the fake.
/// </summary>
public sealed class RtfqFixture : IAsyncLifetime
{
    public const string GrantedToken = "granted-token-0123456789";
    public const string UngrantedToken = "ungranted-token-9876543210";
    public const int SourceMaxRows = 100;
    public const int SeededRows = 250;

    PostgreSqlContainer _postgres = null!;
    RtfqServer _server = null!;
    string _workDir = null!;

    public string BaseAddress { get; private set; } = "";
    public string StateDir { get; private set; } = "";
    public string AuditPath => Path.Combine(StateDir, "audit.jsonl");

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-tests", Guid.NewGuid().ToString("n")[..8]);
        StateDir = Path.Combine(_workDir, "state");
        Directory.CreateDirectory(StateDir);

        _postgres = new PostgreSqlBuilder("postgres:17-alpine")
            .WithDatabase("orders")
            .Build();

        await _postgres.StartAsync();
        await SeedAsync(_postgres.GetConnectionString());

        var (certPath, keyPath) = WriteSelfSignedCertificate(_workDir);

        var config = new RtfqConfig
        {
            Server = new ServerSection
            {
                // Port 0: the OS picks, so parallel runs cannot collide.
                Listen = "127.0.0.1:0",
                Tls = new TlsSection { CertPath = certPath, KeyPath = keyPath },
                Auth = new AuthSection
                {
                    Mode = "token",
                    Tokens =
                    [
                        new TokenSection
                        {
                            Id = "granted",
                            Secret = GrantedToken,
                            SecretWasReference = true,
                            Grants = new Dictionary<string, AccessLevel> { ["orders"] = AccessLevel.Read },
                        },
                        new TokenSection
                        {
                            Id = "ungranted",
                            Secret = UngrantedToken,
                            SecretWasReference = true,
                            Grants = new Dictionary<string, AccessLevel>(),
                        },
                    ],
                },
            },
            Defaults = new DefaultsSection { MaxRows = 1000, StatementTimeout = TimeSpan.FromSeconds(10) },
            Sources =
            [
                new SourceSection
                {
                    Name = "orders",
                    Kind = "postgres",
                    Dsn = _postgres.GetConnectionString(),
                    DsnWasReference = true,
                    Description = "Order lifecycle",
                    Access = AccessLevel.Read,
                    Schemas = ["public"],
                    MaxRows = SourceMaxRows,
                },
            ],
        };

        _server = await RtfqServer.StartAsync(config, StateDir);
        BaseAddress = _server.BaseAddress;
    }

    static async Task SeedAsync(string connectionString)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"""
            CREATE TABLE orders (
                id         int primary key,
                customer   text not null,
                total      numeric(10,2) not null,
                vip        boolean not null,
                created_at timestamptz not null default now()
            );
            INSERT INTO orders (id, customer, total, vip)
            SELECT g, 'customer-' || g, (g * 1.5)::numeric(10,2), g % 7 = 0
            FROM generate_series(1, {SeededRows}) g;
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static (string CertPath, string KeyPath) WriteSelfSignedCertificate(string directory)
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

        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));

        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    public RtfqClient Client(string token) => new(BaseAddress, token, skipCertificateValidation: true);

    /// <summary>Audit entries written so far, newest last.</summary>
    public List<JsonElement> ReadAudit()
    {
        var entries = new List<JsonElement>();
        if (!File.Exists(AuditPath)) return entries;

        // The server holds the file open for append; read with sharing.
        using var stream = new FileStream(AuditPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0) continue;
            entries.Add(JsonDocument.Parse(line).RootElement.Clone());
        }
        return entries;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}

[CollectionDefinition(nameof(RtfqCollection))]
public sealed class RtfqCollection : ICollectionFixture<RtfqFixture>;

[Collection(nameof(RtfqCollection))]
public sealed class PostgresEndToEndTests(RtfqFixture fixture)
{
    // --- M0 exit criterion 1: a query crosses the wire over TLS with token auth --

    [Fact]
    public async Task A_query_returns_rows_over_tls()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT id, customer, total, vip FROM orders WHERE id <= 5");

        Assert.StartsWith("https://", fixture.BaseAddress, StringComparison.Ordinal);
        Assert.Equal(5, result.RowCount);
        Assert.False(result.Truncated);
        Assert.Equal(["id", "customer", "total", "vip"], result.Columns.Select(c => c.Name));

        var firstRow = result.Rows[0]!.AsArray();
        Assert.Equal(1, firstRow[0]!.GetValue<int>());
        Assert.Equal("customer-1", firstRow[1]!.GetValue<string>());
        Assert.False(firstRow[3]!.GetValue<bool>());
    }

    [Fact]
    public async Task Nulls_survive_the_wire_as_nulls()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT NULL::text AS nothing, 42 AS answer");

        var row = result.Rows[0]!.AsArray();
        Assert.Null(row[0]);
        Assert.Equal(42, row[1]!.GetValue<int>());
    }

    // --- M0 exit criterion 2: over-cap results are truncated and flagged --------

    [Fact]
    public async Task Exceeding_the_configured_cap_truncates_and_says_so()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT * FROM orders");

        Assert.True(result.Truncated, "250 rows through a cap of 100 must be flagged truncated");
        Assert.Equal(RtfqFixture.SourceMaxRows, result.RowCount);
    }

    [Fact]
    public async Task A_caller_may_lower_its_own_cap()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT * FROM orders", maxRows: 10);

        Assert.Equal(10, result.RowCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task A_caller_may_not_raise_its_own_cap()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT * FROM orders", maxRows: 10_000);

        Assert.Equal(RtfqFixture.SourceMaxRows, result.RowCount);
        Assert.True(result.Truncated);
    }

    [Fact]
    public async Task A_result_that_exactly_fills_the_cap_is_not_reported_as_truncated()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", $"SELECT * FROM orders LIMIT {RtfqFixture.SourceMaxRows}");

        Assert.Equal(RtfqFixture.SourceMaxRows, result.RowCount);
        Assert.False(result.Truncated, "exactly-full is not the same as clipped");
    }

    // --- M0 exit criterion 3: an ungranted token is refused, with a typed code ---

    [Fact]
    public async Task A_token_without_a_grant_is_refused_and_audited()
    {
        using var client = fixture.Client(RtfqFixture.UngrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.QueryAsync("orders", "SELECT 1"));

        Assert.Equal(ErrorCodes.SourceUnknown, ex.Code);
        Assert.Equal(404, ex.StatusCode);

        var refusal = fixture.ReadAudit().LastOrDefault(e =>
            e.TryGetProperty("error_code", out var code) && code.GetString() == ErrorCodes.SourceUnknown);

        Assert.NotEqual(default, refusal);
        Assert.Equal("refused", refusal.GetProperty("classification").GetString());
        Assert.Equal("ungranted", refusal.GetProperty("token_id").GetString());
    }

    [Fact]
    public async Task An_unrecognised_token_is_refused()
    {
        using var client = fixture.Client("not-a-real-token");

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.QueryAsync("orders", "SELECT 1"));

        Assert.Equal(ErrorCodes.TokenInvalid, ex.Code);
        Assert.Equal(401, ex.StatusCode);
    }

    [Fact]
    public async Task An_ungranted_caller_cannot_enumerate_sources()
    {
        using var client = fixture.Client(RtfqFixture.UngrantedToken);

        var result = await client.ListSourcesAsync();

        Assert.Empty(result.Sources);
    }

    // --- everything else --------------------------------------------------------

    [Fact]
    public async Task Sources_report_effective_access_and_capabilities()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.ListSourcesAsync();

        var source = Assert.Single(result.Sources);
        Assert.Equal("orders", source.Name);
        Assert.Equal("postgres", source.Kind);
        Assert.Equal("read", source.EffectiveAccess);
        Assert.Contains("transactional_ddl", source.Capabilities);
    }

    [Fact]
    public async Task A_statement_the_engine_rejects_comes_back_as_source_rejected()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.QueryAsync("orders", "SELECT * FROM table_that_does_not_exist"));

        Assert.Equal(ErrorCodes.SourceRejected, ex.Code);
        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task An_empty_statement_is_refused_before_it_reaches_the_source()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.QueryAsync("orders", "   "));

        Assert.Equal(ErrorCodes.StatementEmpty, ex.Code);
    }

    [Fact]
    public async Task Every_successful_query_is_audited_with_its_row_count()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        await client.QueryAsync("orders", "SELECT id FROM orders WHERE id <= 3");

        var entry = fixture.ReadAudit().Last(e =>
            e.GetProperty("operation").GetString() == "query" &&
            e.GetProperty("outcome").GetString() == "ok");

        Assert.Equal("granted", entry.GetProperty("token_id").GetString());
        Assert.Equal("orders", entry.GetProperty("source").GetString());
        Assert.Equal("read", entry.GetProperty("classification").GetString());
        Assert.Equal(3, entry.GetProperty("row_count").GetInt32());
        Assert.False(entry.GetProperty("truncated").GetBoolean());
        Assert.Contains("SELECT id FROM orders", entry.GetProperty("statement").GetString()!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Health_needs_no_token()
    {
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
        };
        using var http = new HttpClient(handler) { BaseAddress = new Uri(fixture.BaseAddress) };

        using var response = await http.GetAsync("/health");

        Assert.True(response.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Plain_http_cannot_talk_to_a_tls_listener()
    {
        using var http = new HttpClient { BaseAddress = new Uri(fixture.BaseAddress.Replace("https://", "http://", StringComparison.Ordinal)) };

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync("/health"));
    }
}
