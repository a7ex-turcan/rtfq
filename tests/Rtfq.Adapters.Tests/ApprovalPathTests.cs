using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Npgsql;
using Rtfq.Client;
using Rtfq.Contracts;
using Rtfq.Server;
using Rtfq.Server.Approval;
using Rtfq.Server.Configuration;
using Testcontainers.PostgreSql;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// M4 end to end against a real PostgreSQL: the human gate and the time-boxed
/// unlock.
///
/// The container is shared and servers are cheap, so tests that need a different
/// timeout or a different approval provider start their own. That matters for
/// two of the claims made below — that an unanswered approval lapses, and that a
/// restart re-locks — neither of which can be tested by poking at a process that
/// never stops.
/// </summary>
public sealed class ApprovalFixture : IAsyncLifetime
{
    public const string WriterToken = "approve-writer-0123456789";
    public const string ReaderToken = "approve-reader-0123456789";

    PostgreSqlContainer _postgres = null!;
    string _workDir = null!;

    public string Dsn { get; private set; } = "";

    public async Task InitializeAsync()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "rtfq-approve", Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(_workDir);

        _postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("shop").Build();
        await _postgres.StartAsync();
        Dsn = _postgres.GetConnectionString();

        await ExecuteAsync("""
            CREATE TABLE orders (id int primary key, status text not null, note text);
            INSERT INTO orders SELECT g, 'stuck', NULL FROM generate_series(1, 20) g;

            CREATE TABLE stock (id int primary key, qty int not null);
            INSERT INTO stock SELECT g, g * 10 FROM generate_series(1, 20) g;
            """);
    }

    /// <param name="approvalTtl">Also the window the local provider gives a human.</param>
    /// <param name="approvals">Swaps the provider out. Nothing else in the server changes.</param>
    public async Task<Deployment> StartAsync(
        TimeSpan? approvalTtl = null, IApprovalProvider? approvals = null)
    {
        var stateDir = Path.Combine(_workDir, Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(stateDir);
        var (cert, key) = Certificate(stateDir);

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
                        Token("writer", WriterToken, AccessLevel.Schema, AccessLevel.Schema),
                        Token("reader", ReaderToken, AccessLevel.Read, AccessLevel.Read),
                    ],
                },
            },
            Defaults = new DefaultsSection
            {
                MaxAffectedRows = 50,
                StatementTimeout = TimeSpan.FromSeconds(10),
                LockTimeout = TimeSpan.FromSeconds(3),
                WriteHandleTtl = TimeSpan.FromSeconds(30),
                ApprovalTtl = approvalTtl ?? TimeSpan.FromMinutes(2),
            },
            Sources =
            [
                Source("shop", "public.orders", requireApproval: true, requireUnlock: false),
                Source("vault", "public.stock", requireApproval: false, requireUnlock: true),
            ],
        };

        return new Deployment(await RtfqServer.StartAsync(config, stateDir, approvals), stateDir);
    }

    SourceSection Source(string name, string writable, bool requireApproval, bool requireUnlock) => new()
    {
        Name = name,
        Kind = "postgres",
        Dsn = Dsn,
        DsnWasReference = true,
        Access = AccessLevel.Schema,
        Schemas = ["public"],
        WritableTables = [writable],
        RequireApproval = requireApproval,
        RequireUnlock = requireUnlock,
    };

    static TokenSection Token(string id, string secret, AccessLevel shop, AccessLevel vault) => new()
    {
        Id = id,
        Secret = secret,
        SecretWasReference = true,
        Grants = new Dictionary<string, AccessLevel> { ["shop"] = shop, ["vault"] = vault },
    };

    /// <summary>
    /// A running server together with the directory it journals into, so a test
    /// can read back what it wrote instead of guessing at the path.
    /// </summary>
    public sealed record Deployment(RtfqServer Server, string StateDir) : IAsyncDisposable
    {
        /// <summary>The server holds the journal open for append, so this shares it rather than taking it exclusively.</summary>
        public string AuditText()
        {
            var path = Path.Combine(StateDir, "audit.jsonl");
            if (!File.Exists(path)) return "";

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }

        public ValueTask DisposeAsync() => Server.DisposeAsync();
    }

    public static RtfqClient Client(Deployment deployment, string token) =>
        new(deployment.Server.BaseAddress, token, skipCertificateValidation: true);

    /// <summary>Reads the database directly, so an assertion about the data does not go through the thing under test.</summary>
    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(Dsn);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync();
        return (T)Convert.ChangeType(value!, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <param name="timeout">
    /// Kept short deliberately: several tests use this to prove that nothing is
    /// holding a lock, and an unbounded wait would turn that proof into a hang.
    /// </param>
    public async Task ExecuteAsync(string sql, TimeSpan? timeout = null)
    {
        var builder = new NpgsqlConnectionStringBuilder(Dsn)
        {
            CommandTimeout = (int)(timeout ?? TimeSpan.FromSeconds(30)).TotalSeconds,
        };

        await using var conn = new NpgsqlConnection(builder.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    static (string Cert, string Key) Certificate(string directory)
    {
        var certPath = Path.Combine(directory, "tls.crt");
        var keyPath = Path.Combine(directory, "tls.key");

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        File.WriteAllText(certPath, certificate.ExportCertificatePem());
        File.WriteAllText(keyPath, rsa.ExportPkcs8PrivateKeyPem());
        return (certPath, keyPath);
    }

    public async Task DisposeAsync()
    {
        await _postgres.DisposeAsync();
        try { Directory.Delete(_workDir, recursive: true); } catch (IOException) { }
    }
}

[CollectionDefinition(nameof(ApprovalCollection))]
public sealed class ApprovalCollection : ICollectionFixture<ApprovalFixture>;

[Collection(nameof(ApprovalCollection))]
public sealed class ApprovalPathTests(ApprovalFixture fixture) : IAsyncLifetime
{
    ApprovalFixture.Deployment _server = null!;

    public async Task InitializeAsync() => _server = await fixture.StartAsync();
    public async Task DisposeAsync() => await _server.DisposeAsync();

    RtfqClient Writer() => ApprovalFixture.Client(_server, ApprovalFixture.WriterToken);
    RtfqClient Reader() => ApprovalFixture.Client(_server, ApprovalFixture.ReaderToken);

    static async Task<RtfqClientException> Refused(Func<Task> action) =>
        await Assert.ThrowsAsync<RtfqClientException>(action);

    // --- what a proposal holds while a human thinks ----------------------------

    [Fact]
    public async Task An_approval_required_proposal_holds_no_lock_on_the_rows_it_touched()
    {
        using var client = Writer();

        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET note = 'held?' WHERE id = 1");

        Assert.True(proposal.RequiresApproval);

        // The whole design turns on this. If the proposal were still holding its
        // transaction, this would block until the lock timeout and throw.
        await fixture.ExecuteAsync("UPDATE orders SET note = 'not held' WHERE id = 1", TimeSpan.FromSeconds(5));

        Assert.Equal("not held", await fixture.ScalarAsync<string>("SELECT note FROM orders WHERE id = 1"));
    }

    [Fact]
    public async Task A_proposal_awaiting_a_human_changes_nothing_by_itself()
    {
        using var client = Writer();

        await client.ProposeWriteAsync("shop", "UPDATE orders SET status = 'paid' WHERE id = 2");

        Assert.Equal("stuck", await fixture.ScalarAsync<string>("SELECT status FROM orders WHERE id = 2"));
    }

    [Fact]
    public async Task Committing_before_anyone_answers_is_pending_and_the_handle_survives()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET status = 'paid' WHERE id = 3");

        var first = await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal("pending", first.Outcome);
        Assert.Equal("stuck", await fixture.ScalarAsync<string>("SELECT status FROM orders WHERE id = 3"));

        // Polling must not consume the handle, or an impatient agent would destroy
        // the change it is waiting for.
        var second = await client.CommitWriteAsync(proposal.Handle);
        Assert.Equal("pending", second.Outcome);
    }

    // --- can the person reading this act on it? --------------------------------

    [Fact]
    public async Task A_proposal_names_the_request_a_human_has_to_answer()
    {
        using var client = Writer();

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'who' WHERE id = 16");

        Assert.True(proposal.RequiresApproval);
        Assert.False(string.IsNullOrWhiteSpace(proposal.ApprovalId));

        // The id must be the one actually waiting, not merely non-empty.
        Assert.Contains((await client.ListApprovalsAsync()).Pending, p => p.Id == proposal.ApprovalId);
    }

    [Fact]
    public async Task A_proposal_says_what_a_person_must_run_and_that_nobody_was_told()
    {
        using var client = Writer();

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'how' WHERE id = 17");

        // "A human has been asked" was the old wording, and it left the person
        // reading it with nothing to do. Nothing notifies the approver, and
        // saying so is the difference between a change being approved and a
        // change silently lapsing.
        Assert.NotNull(proposal.Hint);
        Assert.Contains("rtfq approvals --approve", proposal.Hint!);
        Assert.Contains(proposal.ApprovalId!, proposal.Hint!);
        Assert.Contains("NOBODY HAS BEEN NOTIFIED", proposal.Hint!);
    }

    [Fact]
    public async Task A_pending_commit_repeats_the_command_rather_than_only_saying_pending()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'wait' WHERE id = 18");

        var pending = await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal("pending", pending.Outcome);
        Assert.Equal(proposal.ApprovalId, pending.ApprovalId);
        Assert.NotNull(pending.ExpiresAt);

        // An agent polling this is the one most likely to be asked "so what do I
        // do?", so the answer travels with every pending response, not just the
        // first.
        Assert.Contains("rtfq approvals --approve", pending.Hint!);
        Assert.Contains(proposal.ApprovalId!, pending.Hint!);
    }

    [Fact]
    public async Task A_proposal_needing_no_approval_names_no_approver_and_no_id()
    {
        using var client = Writer();

        await client.UnlockAsync("vault", "write", "5m");
        var proposal = await client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 7 WHERE id = 19");

        Assert.False(proposal.RequiresApproval);
        Assert.Null(proposal.ApprovalId);
        Assert.DoesNotContain("approvals --approve", proposal.Hint ?? "");
    }

    // --- the answer ---------------------------------------------------------------

    [Fact]
    public async Task An_approved_change_commits_and_records_who_said_yes()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET status = 'paid' WHERE id = 4");

        var pending = await client.ListApprovalsAsync();
        var mine = Assert.Single(pending.Pending, p => p.Statement.Contains("id = 4"));
        await client.DecideApprovalAsync(mine.Id, approved: true, approver: "alex");

        var result = await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal("committed", result.Outcome);
        Assert.Equal("alex", result.Approver);
        Assert.Equal("paid", await fixture.ScalarAsync<string>("SELECT status FROM orders WHERE id = 4"));
    }

    [Fact]
    public async Task A_denied_change_is_refused_and_the_data_is_untouched()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "DELETE FROM orders WHERE id = 5");

        var pending = await client.ListApprovalsAsync();
        var mine = Assert.Single(pending.Pending, p => p.Statement.Contains("id = 5"));
        await client.DecideApprovalAsync(mine.Id, approved: false, approver: "alex", reason: "wrong table");

        var error = await Refused(() => client.CommitWriteAsync(proposal.Handle));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Contains("denied by alex", error.Message);
        Assert.Contains("wrong table", error.Message);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE id = 5"));
    }

    [Fact]
    public async Task A_denied_handle_is_spent_rather_than_left_lying_around_for_a_second_try()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "DELETE FROM orders WHERE id = 6");

        var mine = Assert.Single((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("id = 6"));
        await client.DecideApprovalAsync(mine.Id, approved: false, approver: "alex");

        await Refused(() => client.CommitWriteAsync(proposal.Handle));
        var second = await Refused(() => client.CommitWriteAsync(proposal.Handle));

        Assert.Equal(ErrorCodes.SourceUnknown, second.Code);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE id = 6"));
    }

    [Fact]
    public async Task Aborting_takes_the_question_back_off_the_queue()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'abandoned' WHERE id = 7");

        Assert.Contains((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("id = 7"));

        await client.AbortWriteAsync(proposal.Handle);

        // Nobody should be asked to decide something that can no longer happen.
        Assert.DoesNotContain((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("id = 7"));
    }

    // --- the approval describes one change, not a shape of change --------------------

    [Fact]
    public async Task An_approval_stops_applying_once_the_data_moves_underneath_it()
    {
        using var client = Writer();
        var proposal = await client.ProposeWriteAsync("shop",
            "UPDATE orders SET status = 'paid' WHERE status = 'stuck' AND id IN (8, 9)");

        var mine = Assert.Single((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("(8, 9)"));
        Assert.Equal(2, mine.AffectedRows);
        await client.DecideApprovalAsync(mine.Id, approved: true, approver: "alex");

        // Somebody else gets there first, so the yes now describes a change that no
        // longer exists: two rows were approved, one remains.
        await fixture.ExecuteAsync("UPDATE orders SET status = 'paid' WHERE id = 8");

        var error = await Refused(() => client.CommitWriteAsync(proposal.Handle));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Contains("changed after this was approved", error.Message);

        // And the refusal rolled back: row 9 is untouched rather than half-applied.
        Assert.Equal("stuck", await fixture.ScalarAsync<string>("SELECT status FROM orders WHERE id = 9"));
    }

    [Fact]
    public async Task One_yes_settles_one_handle_and_not_its_twin()
    {
        using var client = Writer();

        var first = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'twin' WHERE id = 10");
        var second = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'twin' WHERE id = 10");

        var mine = (await client.ListApprovalsAsync()).Pending.Where(p => p.Statement.Contains("id = 10")).ToList();
        Assert.Equal(2, mine.Count);

        // Identical statements over identical data fingerprint identically, which
        // is exactly the case where one approval must not settle both handles.
        // Which of the two is approved does not matter: one and only one commits.
        await client.DecideApprovalAsync(mine[0].Id, approved: true, approver: "alex");

        var settled = 0;
        var stillWaiting = 0;
        foreach (var handle in new[] { first.Handle, second.Handle })
        {
            var outcome = (await client.CommitWriteAsync(handle)).Outcome;
            if (outcome == "committed") settled++;
            if (outcome == "pending") stillWaiting++;
        }

        Assert.Equal(1, settled);
        Assert.Equal(1, stillWaiting);
    }

    // --- what the approver is shown --------------------------------------------------

    [Fact]
    public async Task The_queue_shows_the_statement_and_the_rows_as_they_are_now()
    {
        using var client = Writer();
        await client.ProposeWriteAsync("shop", "UPDATE orders SET status = 'paid' WHERE id = 11");

        var mine = Assert.Single((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("id = 11"));

        Assert.Equal("UPDATE orders SET status = 'paid' WHERE id = 11", mine.Statement);
        Assert.Equal("public.orders", mine.Target);
        Assert.Equal(1, mine.AffectedRows);

        // Before-images, so the approver sees what is about to be overwritten
        // rather than only what it will become.
        Assert.Contains("stuck", mine.DiffRows);
        Assert.Contains("status", mine.DiffColumns);
    }

    [Fact]
    public async Task A_read_only_token_can_neither_see_the_queue_nor_answer_it()
    {
        using var writer = Writer();
        await writer.ProposeWriteAsync("shop", "UPDATE orders SET note = 'private' WHERE id = 12");

        using var reader = Reader();

        var listing = await Refused(() => reader.ListApprovalsAsync());
        Assert.Equal(ErrorCodes.InsufficientAccess, listing.Code);

        var deciding = await Refused(() => reader.DecideApprovalAsync("anything", true, "reader"));
        Assert.Equal(ErrorCodes.InsufficientAccess, deciding.Code);
    }

    [Fact]
    public async Task Answering_something_that_is_not_waiting_is_a_plain_no()
    {
        using var client = Writer();

        var error = await Refused(() => client.DecideApprovalAsync("not-a-real-id", true, "alex"));
        Assert.Equal(404, error.StatusCode);
    }

    // --- the audit trail ------------------------------------------------------------------

    [Fact]
    public async Task The_journal_records_the_decision_and_the_person_who_made_it()
    {
        await using var deployment = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(deployment, ApprovalFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'audited' WHERE id = 13");
        var mine = Assert.Single((await client.ListApprovalsAsync()).Pending, p => p.Statement.Contains("id = 13"));
        await client.DecideApprovalAsync(mine.Id, approved: true, approver: "alex", reason: "read the diff");
        await client.CommitWriteAsync(proposal.Handle);

        var audit = deployment.AuditText();

        // Proposed, decided, and committed - and the commit line names the person,
        // so "who let this through" is answerable from the journal alone.
        Assert.Contains("awaiting-approval", audit);
        Assert.Contains("\"approver\":\"alex\"", audit);
    }
}

/// <summary>
/// The claims that need a server with a different lifetime or a different
/// provider than the shared one.
/// </summary>
[Collection(nameof(ApprovalCollection))]
public sealed class ApprovalLifetimeTests(ApprovalFixture fixture)
{
    [Fact]
    public async Task An_approval_nobody_answers_lapses_and_changes_nothing()
    {
        await using var server = await fixture.StartAsync(approvalTtl: TimeSpan.FromSeconds(2));
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "DELETE FROM orders WHERE id = 14");

        await Task.Delay(TimeSpan.FromSeconds(3));

        var error = await Assert.ThrowsAsync<RtfqClientException>(() => client.CommitWriteAsync(proposal.Handle));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Contains("in time", error.Message);
        Assert.Equal(1, await fixture.ScalarAsync<int>("SELECT count(*) FROM orders WHERE id = 14"));
    }

    /// <summary>
    /// An approval service that lives outside this process, answering over HTTP.
    /// Stands in for the Slack integration CLAUDE.md says must never be in core.
    /// </summary>
    sealed class StubApprovalService(string approver) : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post)
            {
                Seen.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return Json("""{"request_id":"ext-1"}""");
            }

            if (request.Method == HttpMethod.Delete) return new HttpResponseMessage(HttpStatusCode.NoContent);

            return Json($$"""{"state":"approved","approver":"{{approver}}"}""");
        }

        static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    [Fact]
    public async Task A_second_provider_swaps_in_and_the_broker_never_learns_of_it()
    {
        var stub = new StubApprovalService("slack:alex");
        using var webhook = new WebhookApprovalProvider(
            new HttpClient(stub) { BaseAddress = new Uri("https://approvals.example/") });

        await using var server = await fixture.StartAsync(approvals: webhook);
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        var proposal = await client.ProposeWriteAsync("shop", "UPDATE orders SET note = 'via slack' WHERE id = 15");
        var result = await client.CommitWriteAsync(proposal.Handle);

        Assert.Equal("committed", result.Outcome);
        Assert.Equal("slack:alex", result.Approver);
        Assert.Equal("via slack", await fixture.ScalarAsync<string>("SELECT note FROM orders WHERE id = 15"));

        // The provider got the statement and the before-images, and nothing that
        // reads like a persuasive summary.
        var payload = Assert.Single(stub.Seen);
        Assert.Contains("via slack", payload);
        Assert.Contains("diff_rows", payload);
    }

    [Fact]
    public async Task Under_a_webhook_provider_the_local_queue_says_it_is_not_the_queue()
    {
        var stub = new StubApprovalService("slack:alex");
        using var webhook = new WebhookApprovalProvider(
            new HttpClient(stub) { BaseAddress = new Uri("https://approvals.example/") });

        await using var server = await fixture.StartAsync(approvals: webhook);
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        // An empty list would read as "nothing is waiting", which is the one
        // answer that must not be given when questions are queued elsewhere.
        var error = await Assert.ThrowsAsync<RtfqClientException>(() => client.ListApprovalsAsync());
        Assert.Contains("webhook", error.Message);
    }
}

/// <summary>The time-boxed unlock, against the source that requires one.</summary>
[Collection(nameof(ApprovalCollection))]
public sealed class UnlockPathTests(ApprovalFixture fixture)
{
    static async Task<RtfqClientException> Refused(Func<Task> action) =>
        await Assert.ThrowsAsync<RtfqClientException>(action);

    [Fact]
    public async Task A_locked_source_refuses_the_write_and_says_how_to_open_it()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        var error = await Refused(() => client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 0 WHERE id = 1"));

        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
        Assert.Contains("rtfq unlock vault", error.Message);
        Assert.Equal(10, await fixture.ScalarAsync<int>("SELECT qty FROM stock WHERE id = 1"));
    }

    [Fact]
    public async Task A_locked_source_is_still_readable()
    {
        // Locking reads would break the discovery this server exists for.
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.ReaderToken);

        var result = await client.QueryAsync("vault", "SELECT qty FROM stock WHERE id = 1");
        Assert.Equal(1, result.RowCount);
    }

    [Fact]
    public async Task Unlocking_opens_the_write_path_and_locking_shuts_it_again()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        await client.UnlockAsync("vault", "write", "15m");

        var proposal = await client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 99 WHERE id = 2");
        await client.CommitWriteAsync(proposal.Handle);
        Assert.Equal(99, await fixture.ScalarAsync<int>("SELECT qty FROM stock WHERE id = 2"));

        await client.LockAsync("vault");

        var error = await Refused(() => client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 0 WHERE id = 2"));
        Assert.Contains("is locked", error.Message);
    }

    [Fact]
    public async Task An_unlock_for_write_does_not_quietly_open_schema_changes()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        await client.UnlockAsync("vault", "write", "15m");

        var error = await Refused(() => client.ProposeWriteAsync("vault", "ALTER TABLE stock ADD COLUMN sku text"));
        Assert.Contains("is locked", error.Message);
    }

    [Fact]
    public async Task An_unlock_lapses_on_its_own()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        await client.UnlockAsync("vault", "write", "1s");
        await Task.Delay(TimeSpan.FromSeconds(2));

        var error = await Refused(() => client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 0 WHERE id = 3"));

        Assert.Contains("is locked", error.Message);
        Assert.Equal(30, await fixture.ScalarAsync<int>("SELECT qty FROM stock WHERE id = 3"));
    }

    [Fact]
    public async Task A_restart_re_locks()
    {
        var first = await fixture.StartAsync();
        using (var client = ApprovalFixture.Client(first, ApprovalFixture.WriterToken))
        {
            await client.UnlockAsync("vault", "write", "1h");
            Assert.Single((await client.ListUnlocksAsync()).Unlocked);
        }
        await first.DisposeAsync();

        // Nothing is persisted, so the window cannot outlive the thing it was
        // opened for.
        await using var second = await fixture.StartAsync();
        using var after = ApprovalFixture.Client(second, ApprovalFixture.WriterToken);

        Assert.Empty((await after.ListUnlocksAsync()).Unlocked);

        var error = await Refused(() => after.ProposeWriteAsync("vault", "UPDATE stock SET qty = 0 WHERE id = 4"));
        Assert.Contains("is locked", error.Message);
    }

    [Fact]
    public async Task A_token_that_cannot_write_cannot_open_the_door_either()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.ReaderToken);

        var error = await Refused(() => client.UnlockAsync("vault", "write", "15m"));
        Assert.Equal(ErrorCodes.InsufficientAccess, error.Code);
    }

    [Fact]
    public async Task A_ttl_longer_than_the_ceiling_is_clamped_rather_than_honoured()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        var result = await client.UnlockAsync("vault", "write", "8h");
        var state = Assert.Single(result.Unlocked);

        var expires = DateTimeOffset.Parse(state.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(expires <= DateTimeOffset.UtcNow.AddHours(1).AddMinutes(1),
            $"an 8h unlock should have been clamped to an hour, but expires at {state.ExpiresAt}");
    }

    [Fact]
    public async Task Unlocking_the_source_that_does_not_require_it_changes_nothing_about_the_other()
    {
        await using var server = await fixture.StartAsync();
        using var client = ApprovalFixture.Client(server, ApprovalFixture.WriterToken);

        await client.UnlockAsync("shop", "write", "15m");

        var error = await Refused(() => client.ProposeWriteAsync("vault", "UPDATE stock SET qty = 0 WHERE id = 5"));
        Assert.Contains("is locked", error.Message);
    }
}
