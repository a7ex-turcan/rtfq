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
/// The M3 exit criterion that cannot be tested with a shared fixture: a server
/// that goes away mid-transaction must leave nothing committed.
///
/// This gets its own container and server because it destroys them. Two failure
/// modes are covered — an orderly shutdown, and the connection simply vanishing —
/// because they are rolled back by different things. The first is our code; the
/// second is the database, and relying on it is a claim worth checking rather
/// than assuming.
/// </summary>
public sealed class CrashSafetyTests : IAsyncLifetime
{
    const string Token = "crash-token-0123456789";

    PostgreSqlContainer _postgres = null!;
    string _workDir = null!;
    string _cert = "";
    string _key = "";

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-crash", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_workDir);

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("shop").Build();
        await _postgres.StartAsync();

        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE orders (id int primary key, note text);
            INSERT INTO orders SELECT g, 'original' FROM generate_series(1, 10) g;
            """, conn);
        await cmd.ExecuteNonQueryAsync();

        (_cert, _key) = Certificate(_workDir);
    }

    RtfqConfig Config() => new()
    {
        Server = new ServerSection
        {
            Listen = "127.0.0.1:0",
            Tls = new TlsSection { CertPath = _cert, KeyPath = _key },
            Auth = new AuthSection
            {
                Mode = "token",
                Tokens =
                [
                    new TokenSection
                    {
                        Id = "writer", Secret = Token, SecretWasReference = true,
                        Grants = new Dictionary<string, AccessLevel> { ["shop"] = AccessLevel.Write },
                    },
                ],
            },
        },
        Defaults = new DefaultsSection { MaxAffectedRows = 20, WriteHandleTtl = TimeSpan.FromMinutes(10) },
        Sources =
        [
            new SourceSection
            {
                Name = "shop", Kind = "postgres", Dsn = _postgres.GetConnectionString(), DsnWasReference = true,
                Access = AccessLevel.Write, Schemas = ["public"], WritableTables = ["public.orders"],
            },
        ],
    };

    static (string Cert, string Key) Certificate(string directory)
    {
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

    async Task<int> ChangedRowsAsync()
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT count(*) FROM orders WHERE note <> 'original'", conn);
        return (int)(long)(await cmd.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// An orderly shutdown with a proposal still open. The broker rolls everything
    /// back on disposal, so nothing half-decided survives the process.
    /// </summary>
    [Fact]
    public async Task Shutting_down_with_an_open_proposal_commits_nothing()
    {
        var stateDir = Path.Combine(_workDir, "state-shutdown");
        Directory.CreateDirectory(stateDir);

        var server = await RtfqServer.StartAsync(Config(), stateDir);
        string handle;

        using (var client = new RtfqClient(server.BaseAddress, Token, skipCertificateValidation: true))
        {
            var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'changed' WHERE id <= 3");
            handle = proposal.Handle;
            Assert.Equal(3, proposal.AffectedRows);
        }

        // Nothing settled it. Shut the server down anyway.
        await server.DisposeAsync();

        Assert.Equal(0, await ChangedRowsAsync());
        Assert.NotEmpty(handle);
    }

    /// <summary>
    /// The harder case: the process does not get to clean up. Simulated by killing
    /// the backend PostgreSQL is holding the transaction on, which is what the
    /// database sees when a server is SIGKILLed or its host disappears.
    ///
    /// The rollback here is the database's, not ours — which is exactly why it is
    /// worth a test rather than an assumption.
    /// </summary>
    [Fact]
    public async Task A_connection_that_vanishes_mid_transaction_commits_nothing()
    {
        var stateDir = Path.Combine(_workDir, "state-kill");
        Directory.CreateDirectory(stateDir);

        var server = await RtfqServer.StartAsync(Config(), stateDir);

        using (var client = new RtfqClient(server.BaseAddress, Token, skipCertificateValidation: true))
        {
            var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'vanished' WHERE id >= 8");
            Assert.Equal(3, proposal.AffectedRows);

            // Terminate the backend holding the uncommitted transaction, without
            // giving anything a chance to roll back politely.
            await using var admin = new NpgsqlConnection(_postgres.GetConnectionString());
            await admin.OpenAsync();
            await using var kill = new NpgsqlCommand("""
                SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                WHERE state = 'idle in transaction' AND pid <> pg_backend_pid()
                """, admin);
            await kill.ExecuteNonQueryAsync();

            // The handle is now backed by a dead connection. Committing must fail
            // rather than appear to succeed.
            await Assert.ThrowsAnyAsync<Exception>(() => client.CommitWriteAsync(proposal.Handle));
        }

        Assert.Equal(0, await ChangedRowsAsync());

        await server.DisposeAsync();
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }
}
