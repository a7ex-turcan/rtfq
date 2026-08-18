using System.Net;
using System.Text;
using Rtfq.Contracts;
using Rtfq.Server.Approval;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;

namespace Rtfq.Server.Tests;

/// <summary>
/// The two gates M4 adds, tested where they make their decisions rather than
/// through a database.
///
/// Written the same way as M3's suite: every test is a way past the gate, not a
/// demonstration that the gate exists. The bar for both of these is that an
/// unanswered question is never a yes.
/// </summary>
public sealed class LocalApprovalProviderTests
{
    static ApprovalContext Context(string statement = "UPDATE orders SET status = 'paid' WHERE id = 1") =>
        new("shop", "agent", "public.orders", "mutation", statement, 1, ["id", "status"], "[[1,\"stuck\"]]", "abc123");

    [Fact]
    public async Task An_unanswered_request_is_pending_not_approved()
    {
        var provider = new LocalApprovalProvider(TimeSpan.FromMinutes(5));
        var id = await provider.RequestAsync(Context(), default);

        var decision = await provider.DecisionAsync(id, default);
        Assert.Equal(ApprovalState.Pending, decision.State);
    }

    [Fact]
    public async Task A_request_nobody_answered_in_time_expires_rather_than_lingering()
    {
        var provider = new LocalApprovalProvider(TimeSpan.FromMilliseconds(40));
        var id = await provider.RequestAsync(Context(), default);

        await Task.Delay(120);

        var decision = await provider.DecisionAsync(id, default);
        Assert.Equal(ApprovalState.Expired, decision.State);

        // And it drops out of the queue a human is looking at, so nobody answers
        // a question that can no longer be acted on.
        Assert.Empty(provider.Pending());
    }

    [Fact]
    public async Task An_unknown_id_is_denied_not_pending()
    {
        // Fail closed: a lost request must not leave a commit waiting on an
        // answer that can never arrive.
        var provider = new LocalApprovalProvider(TimeSpan.FromMinutes(5));
        var decision = await provider.DecisionAsync("does-not-exist", default);

        Assert.Equal(ApprovalState.Denied, decision.State);
    }

    [Fact]
    public async Task A_decision_cannot_be_changed_once_it_is_made()
    {
        var provider = new LocalApprovalProvider(TimeSpan.FromMinutes(5));
        var id = await provider.RequestAsync(Context(), default);

        Assert.True(provider.Decide(id, approved: true, "alex", null));

        // By now the commit may already have acted on the yes. Taking it back
        // afterwards would be a decision about something that already happened.
        Assert.False(provider.Decide(id, approved: false, "alex", "changed my mind"));
        Assert.Equal(ApprovalState.Approved, (await provider.DecisionAsync(id, default)).State);
    }

    [Fact]
    public async Task A_withdrawn_request_stops_being_asked_about()
    {
        var provider = new LocalApprovalProvider(TimeSpan.FromMinutes(5));
        var id = await provider.RequestAsync(Context(), default);

        await provider.WithdrawAsync(id, default);

        Assert.Empty(provider.Pending());
        Assert.Equal(ApprovalState.Denied, (await provider.DecisionAsync(id, default)).State);
    }

    [Fact]
    public async Task The_queue_carries_the_statement_and_the_rows_and_nothing_persuasive()
    {
        var provider = new LocalApprovalProvider(TimeSpan.FromMinutes(5));
        await provider.RequestAsync(Context("DELETE FROM orders WHERE status = 'stuck'"), default);

        var pending = Assert.Single(provider.Pending());

        // The type has no field an agent could write prose into. This asserts the
        // shape rather than the value, because the protection is structural: there
        // is nowhere for a summary to go.
        Assert.Equal("DELETE FROM orders WHERE status = 'stuck'", pending.Context.Statement);
        Assert.DoesNotContain(
            typeof(ApprovalContext).GetProperties(),
            p => p.Name is "Summary" or "Description" or "Explanation" or "Reason");
    }
}

/// <summary>
/// The second provider. Its existence is the point: one implementation is not a
/// seam, and these tests are about what happens when the thing on the other end
/// of the seam misbehaves.
/// </summary>
public sealed class WebhookApprovalProviderTests
{
    sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(respond(request));
        }
    }

    static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    static WebhookApprovalProvider Provider(Stub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("https://approvals.example/") });

    static ApprovalContext Context() =>
        new("shop", "agent", "public.orders", "mutation", "DELETE FROM orders WHERE id = 1", 1, ["id"], "[[1]]", "f1");

    [Fact]
    public async Task A_decision_the_endpoint_reports_is_carried_through_with_its_approver()
    {
        var stub = new Stub(_ => Json("""{"state":"approved","approver":"alex","reason":"checked the diff"}"""));
        using var provider = Provider(stub);

        var decision = await provider.DecisionAsync("r1", default);

        Assert.Equal(ApprovalState.Approved, decision.State);
        Assert.Equal("alex", decision.Approver);
    }

    [Fact]
    public async Task An_endpoint_that_is_down_yields_no_approval()
    {
        var stub = new Stub(_ => throw new HttpRequestException("connection refused"));
        using var provider = Provider(stub);

        var decision = await provider.DecisionAsync("r1", default);

        // Not approved, and deliberately not denied either: the handle expires on
        // its own, and a flapping endpoint should not destroy a proposal.
        Assert.Equal(ApprovalState.Pending, decision.State);
        Assert.Contains("unreachable", decision.Reason);
    }

    [Theory]
    [InlineData("""{"state":"yes"}""")]
    [InlineData("""{"state":null}""")]
    [InlineData("""{"approver":"alex"}""")]
    [InlineData("{}")]
    public async Task Anything_the_endpoint_says_that_is_not_a_recognised_verdict_is_not_a_yes(string body)
    {
        var stub = new Stub(_ => Json(body));
        using var provider = Provider(stub);

        Assert.Equal(ApprovalState.Pending, (await provider.DecisionAsync("r1", default)).State);
    }

    [Fact]
    public async Task Garbage_that_is_not_even_json_is_not_a_yes()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html>502 Bad Gateway</html>", Encoding.UTF8, "text/html"),
        });
        using var provider = Provider(stub);

        Assert.Equal(ApprovalState.Pending, (await provider.DecisionAsync("r1", default)).State);
    }

    [Fact]
    public async Task An_endpoint_that_will_not_take_the_request_fails_the_proposal_rather_than_proceeding_unasked()
    {
        var stub = new Stub(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var provider = Provider(stub);

        // Silently continuing here would be the worst possible failure: a change
        // that required approval, running with nobody ever asked.
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.RequestAsync(Context(), default));
    }

    [Fact]
    public async Task Withdrawing_against_a_dead_endpoint_does_not_throw()
    {
        // Withdrawal is a courtesy to the approver, not a gate. It must not turn
        // an abort into an error.
        var stub = new Stub(_ => throw new HttpRequestException("gone"));
        using var provider = Provider(stub);

        await provider.WithdrawAsync("r1", default);
        Assert.Equal(1, stub.Calls);
    }
}

public sealed class UnlockStoreTests
{
    [Fact]
    public void Writing_is_shut_until_somebody_opens_it()
    {
        var store = new UnlockStore();
        Assert.False(store.IsUnlocked("shop", AccessLevel.Write));
    }

    [Fact]
    public void Reading_is_never_locked()
    {
        // Locking reads would break discovery, which is the thing this server is
        // for. The unlock exists for the write path only.
        var store = new UnlockStore();
        Assert.True(store.IsUnlocked("shop", AccessLevel.Read));
    }

    [Fact]
    public void An_unlock_for_write_does_not_open_schema()
    {
        var store = new UnlockStore();
        store.Unlock("shop", AccessLevel.Write, TimeSpan.FromMinutes(5), "alex");

        Assert.True(store.IsUnlocked("shop", AccessLevel.Write));
        Assert.False(store.IsUnlocked("shop", AccessLevel.Schema));
    }

    [Fact]
    public void An_unlock_for_schema_covers_write_as_well()
    {
        var store = new UnlockStore();
        store.Unlock("shop", AccessLevel.Schema, TimeSpan.FromMinutes(5), "alex");

        Assert.True(store.IsUnlocked("shop", AccessLevel.Write));
        Assert.True(store.IsUnlocked("shop", AccessLevel.Schema));
    }

    [Fact]
    public void Unlocking_one_source_does_not_unlock_another()
    {
        var store = new UnlockStore();
        store.Unlock("shop", AccessLevel.Write, TimeSpan.FromMinutes(5), "alex");

        Assert.False(store.IsUnlocked("warehouse", AccessLevel.Write));
    }

    [Fact]
    public async Task An_unlock_closes_the_instant_it_lapses()
    {
        var store = new UnlockStore();
        store.Unlock("shop", AccessLevel.Write, TimeSpan.FromMilliseconds(50), "alex");
        Assert.True(store.IsUnlocked("shop", AccessLevel.Write));

        await Task.Delay(120);

        // Expiry is evaluated on read rather than swept by a timer, so there is no
        // window in which a lapsed unlock is still honoured.
        Assert.False(store.IsUnlocked("shop", AccessLevel.Write));
        Assert.Empty(store.Current());
    }

    [Fact]
    public void A_long_ttl_is_clamped_rather_than_honoured()
    {
        var store = new UnlockStore();
        var state = store.Unlock("shop", AccessLevel.Write, TimeSpan.FromDays(7), "alex");

        // A window measured in days is not a window.
        Assert.True(state.ExpiresAt <= DateTimeOffset.UtcNow + UnlockStore.MaxTtl + TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Locking_shuts_it_immediately()
    {
        var store = new UnlockStore();
        store.Unlock("shop", AccessLevel.Write, TimeSpan.FromMinutes(30), "alex");
        store.Lock("shop");

        Assert.False(store.IsUnlocked("shop", AccessLevel.Write));
    }

    [Fact]
    public void A_fresh_process_starts_locked()
    {
        // The store holds nothing on disk, so this is what a restart looks like.
        // Asserted rather than assumed, because "unlocks do not survive a restart"
        // is a claim the docs make.
        var before = new UnlockStore();
        before.Unlock("shop", AccessLevel.Write, TimeSpan.FromMinutes(30), "alex");

        var after = new UnlockStore();
        Assert.False(after.IsUnlocked("shop", AccessLevel.Write));
    }
}

public sealed class ApprovalConfigTests
{
    static ValidationResult Check(string yaml, bool production = false)
    {
        Environment.SetEnvironmentVariable("RTFQ_TOKEN", "t-123");
        Environment.SetEnvironmentVariable("SHOP_DSN", "Host=localhost");
        var load = ConfigLoader.LoadText(yaml);
        Assert.NotNull(load.Config);
        return ConfigValidator.Validate(load.Config!, production);
    }

    const string Base = """
        server:
          listen: 127.0.0.1:7420
          auth:
            mode: token
            tokens:
              - id: agent
                secret: ${env:RTFQ_TOKEN}
                grants:
                  shop: write
        sources:
          - name: shop
            kind: postgres
            dsn: ${env:SHOP_DSN}
            access: write
            writable_tables: [public.orders]
            require_approval: true
            require_unlock: true
        """;

    [Fact]
    public void The_write_gates_are_read_off_the_source()
    {
        Environment.SetEnvironmentVariable("RTFQ_TOKEN", "t-123");
        Environment.SetEnvironmentVariable("SHOP_DSN", "Host=localhost");
        var load = ConfigLoader.LoadText(Base);
        var source = Assert.Single(load.Config!.Sources);

        Assert.True(source.RequireApproval);
        Assert.True(source.RequireUnlock);
    }

    [Fact]
    public void Approvals_are_local_unless_the_config_says_otherwise()
    {
        Environment.SetEnvironmentVariable("RTFQ_TOKEN", "t-123");
        Environment.SetEnvironmentVariable("SHOP_DSN", "Host=localhost");
        var load = ConfigLoader.LoadText(Base);
        Assert.Equal("local", load.Config!.Approval.Mode);
        Assert.DoesNotContain(Check(Base).Diagnostics, d => d.Check.StartsWith("approval.", StringComparison.Ordinal));
    }

    [Fact]
    public void A_webhook_endpoint_is_read_with_its_timeout_and_headers()
    {
        Environment.SetEnvironmentVariable("RTFQ_APPROVAL_KEY", "k-123");
        Environment.SetEnvironmentVariable("RTFQ_TOKEN", "t-123");
        Environment.SetEnvironmentVariable("SHOP_DSN", "Host=localhost");
        var load = ConfigLoader.LoadText(Base + """

            approval:
              mode: webhook
              endpoint: https://approvals.example/rtfq
              timeout: 30s
              headers:
                Authorization: ${env:RTFQ_APPROVAL_KEY}
            """);

        var approval = load.Config!.Approval;
        Assert.Equal("webhook", approval.Mode);
        Assert.Equal(TimeSpan.FromSeconds(30), approval.Timeout);
        Assert.Equal("k-123", approval.Headers["Authorization"]);
        Assert.False(approval.HeadersHadInlineSecret);
    }

    [Fact]
    public void A_webhook_with_no_endpoint_is_rejected_before_anyone_is_waiting_on_it()
    {
        var result = Check(Base + """

            approval:
              mode: webhook
            """);

        Assert.Contains(result.Diagnostics, d => d.Check == "approval.endpoint_missing" && d.Severity == Severity.Error);
    }

    [Fact]
    public void An_unknown_approval_mode_is_an_error_rather_than_a_silent_fallback_to_local()
    {
        // Falling back would mean a deployment that meant to route approvals to
        // Slack quietly queuing them where nobody is looking.
        var result = Check(Base + """

            approval:
              mode: slack
            """);

        Assert.Contains(result.Diagnostics, d => d.Check == "approval.mode_unknown" && d.Severity == Severity.Error);
    }

    [Fact]
    public void A_plain_http_approval_endpoint_is_refused_in_production()
    {
        var result = Check(Base + """

            approval:
              mode: webhook
              endpoint: http://approvals.internal/rtfq
            """, production: true);

        // The reply to that call decides whether a write happens.
        Assert.Contains(result.Diagnostics, d => d.Check == "approval.tls_in_production" && d.Severity == Severity.Error);
    }

    [Fact]
    public void An_inline_approval_credential_is_a_warning_in_development_and_an_error_in_production()
    {
        const string yaml = """

            approval:
              mode: webhook
              endpoint: https://approvals.example/rtfq
              headers:
                Authorization: Bearer sk-live-inline
            """;

        Assert.Contains(Check(Base + yaml).Diagnostics,
            d => d.Check == "approval.header_inline_secret" && d.Severity == Severity.Warning);
        Assert.Contains(Check(Base + yaml, production: true).Diagnostics,
            d => d.Check == "approval.header_inline_secret" && d.Severity == Severity.Error);
    }

    [Fact]
    public void Configuring_a_webhook_no_source_ever_calls_is_pointed_out()
    {
        var withoutApproval = Base.Replace("require_approval: true", "require_approval: false");
        var result = Check(withoutApproval + """

            approval:
              mode: webhook
              endpoint: https://approvals.example/rtfq
            """);

        Assert.Contains(result.Diagnostics, d => d.Check == "approval.unused" && d.Severity == Severity.Warning);
    }
}
