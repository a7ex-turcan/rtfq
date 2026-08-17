using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Data.SqlClient;
using Rtfq.Client;
using Rtfq.Contracts;
using Rtfq.Server;
using Rtfq.Server.Configuration;
using Testcontainers.MsSql;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// The same write path against SQL Server.
///
/// M3's exit criterion is that the adversarial suite passes on <b>every</b>
/// write-capable adapter, so PostgreSQL passing it is half the claim. The cases
/// here are the ones where T-SQL differs — TOP instead of LIMIT, three statement
/// types for what PostgreSQL calls one ALTER, EXEC carrying opaque SQL — plus the
/// gates, which must behave identically because they are not dialect-specific.
/// </summary>
public sealed class SqlServerWriteFixture : IAsyncLifetime
{
    public const string WriterToken = "mssql-writer-0123456789";
    public const string SchemaToken = "mssql-schema-0123456789";
    public const int Cap = 5;
    public const int SeededRows = 40;

    MsSqlContainer _container = null!;
    RtfqServer _server = null!;
    string _workDir = null!;

    public string BaseAddress { get; private set; } = "";
    public string StateDir { get; private set; } = "";

    /// <summary>Points at the dedicated database rather than master.</summary>
    string ConnectionString =>
        new SqlConnectionStringBuilder(_container.GetConnectionString()) { InitialCatalog = "shop" }.ConnectionString;

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-mssql-write", Guid.NewGuid().ToString("n")[..8]);
        StateDir = Path.Combine(_workDir, "state");
        Directory.CreateDirectory(StateDir);

        _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await _container.StartAsync();

        // A dedicated database with READ_COMMITTED_SNAPSHOT on.
        //
        // Not a test convenience. Without it, an open proposal holds exclusive
        // locks and any reader of those rows BLOCKS until the handle settles —
        // where PostgreSQL's MVCC would show them the pre-image. The behaviour is
        // correct either way (nobody sees uncommitted data), but on SQL Server an
        // abandoned handle stalls readers rather than merely holding a connection,
        // which makes the TTL matter far more. RCSI is the usual production
        // answer, and testing against it is testing the sane configuration.
        await using (var master = new SqlConnection(_container.GetConnectionString()))
        {
            await master.OpenAsync();
            await using var create = new SqlCommand(
                "CREATE DATABASE shop; ALTER DATABASE shop SET READ_COMMITTED_SNAPSHOT ON;", master);
            await create.ExecuteNonQueryAsync();
        }

        await using (var conn = new SqlConnection(ConnectionString))
        {
            await conn.OpenAsync();
            await using var cmd = new SqlCommand($"""
                CREATE TABLE orders (
                    id int primary key, status nvarchar(20) not null, total decimal(10,2) not null, note nvarchar(100) null);
                WITH n AS (SELECT TOP ({SeededRows}) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
                           FROM sys.all_objects)
                INSERT INTO orders (id, status, total)
                SELECT i, CASE WHEN i % 4 = 0 THEN N'stuck' ELSE N'paid' END, i * 1.5 FROM n;

                CREATE TABLE audit_trail (id int identity primary key, what nvarchar(100));
                CREATE TABLE payment_tokens (id int primary key, token nvarchar(100) not null);
                INSERT INTO payment_tokens VALUES (1, N'super-secret');
                """, conn);
            await cmd.ExecuteNonQueryAsync();
        }

        var (cert, key) = Certificate(_workDir);

        var config = new RtfqConfig
        {
            Server = new ServerSection
            {
                Listen = "127.0.0.1:0",
                Tls = new TlsSection { CertPath = cert, KeyPath = key },
                Auth = new AuthSection
                {
                    Mode = "token",
                    Tokens =
                    [
                        Token("writer", WriterToken, AccessLevel.Write),
                        Token("schema", SchemaToken, AccessLevel.Schema),
                    ],
                },
            },
            Defaults = new DefaultsSection
            {
                MaxAffectedRows = Cap,
                StatementTimeout = TimeSpan.FromSeconds(15),
                LockTimeout = TimeSpan.FromSeconds(3),
                WriteHandleTtl = TimeSpan.FromMinutes(2),
            },
            Sources =
            [
                new SourceSection
                {
                    Name = "shop",
                    Kind = "mssql",
                    Dsn = ConnectionString,
                    DsnWasReference = true,
                    Access = AccessLevel.Schema,
                    Schemas = ["dbo"],
                    WritableTables = ["dbo.orders"],
                    DenyTables = ["*.payment_tokens"],
                },
            ],
        };

        _server = await RtfqServer.StartAsync(config, StateDir);
        BaseAddress = _server.BaseAddress;
    }

    static TokenSection Token(string id, string secret, AccessLevel grant) => new()
    {
        Id = id,
        Secret = secret,
        SecretWasReference = true,
        Grants = new Dictionary<string, AccessLevel> { ["shop"] = grant },
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

    public RtfqClient Client(string token) => new(BaseAddress, token, skipCertificateValidation: true);

    /// <summary>Reads the database directly, so assertions do not go through the thing under test.</summary>
    public async Task<int> CountAsync(string where)
    {
        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new SqlCommand($"SELECT COUNT(*) FROM {where}", conn);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _container.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition(nameof(SqlServerWriteCollection))]
public sealed class SqlServerWriteCollection : ICollectionFixture<SqlServerWriteFixture>;

[Collection(nameof(SqlServerWriteCollection))]
public sealed class SqlServerWritePathTests(SqlServerWriteFixture fixture)
{
    static async Task<RtfqClientException> Refused(Func<Task> action) =>
        await Assert.ThrowsAsync<RtfqClientException>(action);

    [Fact]
    public async Task A_proposal_changes_nothing_until_it_is_committed()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = N'looked at' WHERE id = 1");

        Assert.Equal(1, proposal.AffectedRows);
        Assert.Equal("dbo.orders", proposal.Target);
        Assert.Equal(0, await fixture.CountAsync("orders WHERE note = N'looked at'"));

        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(1, await fixture.CountAsync("orders WHERE note = N'looked at'"));
    }

    [Fact]
    public async Task A_proposal_carries_the_rows_as_they_were_before()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET status = N'refunded' WHERE id = 2");

        var status = proposal.DiffColumns.FindIndex(c => c.Name == "status");
        Assert.Equal("paid", proposal.DiffSample[0]!.AsArray()[status]!.GetValue<string>());

        await client.AbortWriteAsync(proposal.Handle);
        Assert.Equal(0, await fixture.CountAsync("orders WHERE status = N'refunded'"));
    }

    /// <summary>The exit criterion, in the second dialect.</summary>
    [Fact]
    public async Task One_row_over_the_cap_is_refused_with_the_real_count_and_rolled_back()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);
        var overCap = SqlServerWriteFixture.Cap + 1;

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            $"UPDATE orders SET note = N'over' WHERE id <= {overCap}"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains($"{overCap} rows", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.CountAsync("orders WHERE note = N'over'"));
    }

    [Fact]
    public async Task Aborting_rolls_back()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "DELETE FROM orders WHERE id = 3");
        Assert.Equal(1, proposal.AffectedRows);

        await client.AbortWriteAsync(proposal.Handle);
        Assert.Equal(1, await fixture.CountAsync("orders WHERE id = 3"));
    }

    [Fact]
    public async Task A_handle_is_single_use()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = N'once' WHERE id = 4");
        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(ErrorCodes.SourceUnknown, (await Refused(() => client.CommitWriteAsync(proposal.Handle))).Code);
    }

    // --- the gates, which must not differ by dialect ----------------------------

    [Fact]
    public async Task A_table_that_is_not_on_the_allow_list_is_refused()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "INSERT INTO audit_trail (what) VALUES (N'sneaky')"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Equal(0, await fixture.CountAsync("audit_trail"));
    }

    [Fact]
    public async Task A_denied_table_reached_through_a_subquery_is_still_denied()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = (SELECT token FROM payment_tokens WHERE id = 1) WHERE id = 5"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("payment_tokens", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_unqualified_update_is_refused()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", "UPDATE orders SET note = N'all'"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Equal(0, await fixture.CountAsync("orders WHERE note = N'all'"));
    }

    [Theory]
    [InlineData("UPDATE orders SET note = N'x' WHERE 1=1")]
    [InlineData("DELETE FROM orders WHERE id = 1 OR 2=2")]
    public async Task A_trivially_true_predicate_is_unqualified(string statement)
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        Assert.Equal(ErrorCodes.InsufficientAccess,
            (await Refused(() => client.ProposeWriteAsync("shop", statement))).Code);
    }

    // --- T-SQL's own ways of smuggling ---------------------------------------------

    [Theory]
    [InlineData("DROP TABLE orders")]
    [InlineData("TRUNCATE TABLE orders")]
    [InlineData("EXEC('DELETE FROM orders')")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    [InlineData("SELECT 1 DROP TABLE orders")]          // a batch with no separator
    [InlineData("UPDATE orders SET note = N'a' WHERE id = 6; DROP TABLE orders")]
    public async Task Catastrophic_statements_are_refused_and_the_table_survives(string statement)
    {
        using var client = fixture.Client(SqlServerWriteFixture.SchemaToken);

        await Refused(() => client.ProposeWriteAsync("shop", statement));

        Assert.Equal(SqlServerWriteFixture.SeededRows, await fixture.CountAsync("orders"));
    }

    // --- schema changes -----------------------------------------------------------------

    [Fact]
    public async Task An_additive_schema_change_is_allowed_with_a_schema_grant()
    {
        using var client = fixture.Client(SqlServerWriteFixture.SchemaToken);

        var proposal = await client.ProposeWriteAsync("shop", "ALTER TABLE orders ADD tier int NULL");
        Assert.Equal("schema", proposal.Kind);
        Assert.Null(proposal.AffectedRows);

        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(1, await fixture.CountAsync(
            "INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' AND COLUMN_NAME = 'tier'"));
    }

    /// <summary>
    /// The T-SQL-specific hazard: DROP COLUMN and DROP CONSTRAINT are the same
    /// statement type, separated only by the element kind.
    /// </summary>
    [Fact]
    public async Task Drop_column_is_refused_though_it_shares_a_statement_type_with_drop_constraint()
    {
        using var client = fixture.Client(SqlServerWriteFixture.SchemaToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", "ALTER TABLE orders DROP COLUMN note"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("DROP COLUMN", ex.Message, StringComparison.Ordinal);
        Assert.Equal(1, await fixture.CountAsync(
            "INFORMATION_SCHEMA.COLUMNS WHERE TABLE_NAME = 'orders' AND COLUMN_NAME = 'note'"));
    }

    [Fact]
    public async Task A_write_token_cannot_make_a_schema_change()
    {
        using var client = fixture.Client(SqlServerWriteFixture.WriterToken);

        Assert.Equal(ErrorCodes.InsufficientAccess,
            (await Refused(() => client.ProposeWriteAsync("shop", "ALTER TABLE orders ADD extra int NULL"))).Code);
    }
}
