using Rtfq.Adapters.Postgres;
using Testcontainers.PostgreSql;

namespace Rtfq.Adapters.Tests;

/// <summary>
/// Two PostgreSQL servers: one that speaks TLS, one that does not.
///
/// Every other Postgres fixture in this project runs with SSL off, which is how
/// "transport security was never enabled" shipped from M0 to 0.7.4 without a
/// single failing test: nothing ever asked the adapter to negotiate TLS. Managed
/// PostgreSQL - RDS, Aurora, Azure, Cloud SQL - is the case that matters most in
/// practice and it is TLS-by-default, so it gets its own servers here.
///
/// The TLS server uses the Debian image's snakeoil certificate, which is exactly
/// what a real deployment looks like from the client's side when it has not
/// installed the server's CA: encrypted, and not verifiable.
/// </summary>
public sealed class PostgresTlsFixture : IAsyncLifetime
{
    public PostgreSqlContainer Tls { get; } = new PostgreSqlBuilder("postgres:17")
        .WithDatabase("tls")
        .WithCommand(
            "-c", "ssl=on",
            "-c", "ssl_cert_file=/etc/ssl/certs/ssl-cert-snakeoil.pem",
            "-c", "ssl_key_file=/etc/ssl/private/ssl-cert-snakeoil.key")
        .Build();

    public PostgreSqlContainer Plain { get; } = new PostgreSqlBuilder("postgres:17-alpine")
        .WithDatabase("plain")
        .Build();

    public Task InitializeAsync() => Task.WhenAll(Tls.StartAsync(), Plain.StartAsync());

    public async Task DisposeAsync()
    {
        await Tls.DisposeAsync();
        await Plain.DisposeAsync();
    }
}

public sealed class PostgresTlsTests(PostgresTlsFixture fixture) : IClassFixture<PostgresTlsFixture>
{
    /// <summary>
    /// Asks the server, not the client, whether this connection is encrypted.
    /// "It connected" is not evidence of TLS: a Prefer that silently fell back to
    /// plaintext connects too.
    /// </summary>
    const string IsEncrypted = "SELECT ssl FROM pg_stat_ssl WHERE pid = pg_backend_pid()";

    static async Task<bool> ConnectsEncrypted(string dsn)
    {
        await using var adapter = new PostgresAdapter("pg", dsn, ["public"], TimeSpan.FromSeconds(15));
        var result = await adapter.ExecuteReadAsync(
            IsEncrypted, new ReadOptions(MaxRows: 1, TimeSpan.FromSeconds(15)), CancellationToken.None);

        Assert.Equal(1, result.RowCount);
        return result.Rows[0]![0]!.GetValue<bool>();
    }

    static string With(PostgreSqlContainer server, string ssl) =>
        server.GetConnectionString().TrimEnd(';') + ";" + ssl;

    // --- the reported case ---------------------------------------------------

    [Fact]
    public async Task Ssl_mode_require_connects_and_the_server_confirms_it_is_encrypted()
    {
        // The field report's DSN, verbatim in shape: Aurora with SSL Mode=Require
        // and Trust Server Certificate=true. Failed client-side, before reaching
        // the server, in every release up to and including 0.7.4.
        var encrypted = await ConnectsEncrypted(
            With(fixture.Tls, "SSL Mode=Require;Trust Server Certificate=true"));

        Assert.True(encrypted);
    }

    // --- the fix must not break or weaken anything else ---------------------

    [Fact]
    public async Task Ssl_mode_disable_still_connects_in_plaintext()
    {
        // Enabling the capability must not force it. The DSN decides.
        var encrypted = await ConnectsEncrypted(With(fixture.Tls, "SSL Mode=Disable"));

        Assert.False(encrypted);
    }

    [Fact]
    public async Task No_ssl_mode_uses_tls_when_the_server_offers_it()
    {
        // Npgsql's default is Prefer. Pinned: against a TLS-capable server, a DSN
        // that says nothing about SSL gets an encrypted connection.
        var encrypted = await ConnectsEncrypted(fixture.Tls.GetConnectionString());

        Assert.True(encrypted);
    }

    [Fact]
    public async Task No_ssl_mode_falls_back_to_plaintext_when_the_server_has_no_tls()
    {
        // And pinned the other way: Prefer against a server with SSL off still
        // connects. This is the path every existing fixture exercises, and it
        // changed underneath them - before the fix Prefer never tried TLS at all;
        // now it asks, is refused, and falls back.
        var encrypted = await ConnectsEncrypted(fixture.Plain.GetConnectionString());

        Assert.False(encrypted);
    }

    [Fact]
    public async Task Verify_full_against_an_untrusted_certificate_is_still_refused()
    {
        // The adversarial one. "Enable transport security" must mean "able to
        // speak TLS", not "trust whatever answers". A certificate nobody has
        // vouched for fails verification, and fails for that reason - not for the
        // reason this whole file exists.
        var ex = await Assert.ThrowsAsync<AdapterException>(() =>
            ConnectsEncrypted(With(fixture.Tls, "SSL Mode=VerifyFull")));

        var chain = string.Join(" | ", Unwrap(ex).Select(e => e.Message));

        // Refused during the handshake, because of the certificate...
        Assert.Contains("remote certificate was rejected", chain);
        Assert.DoesNotContain("Transport security hasn't been enabled", chain);

        // ...and classified as the source being unavailable, not as the caller's
        // statement being wrong - the agent has nothing to fix in its SQL.
        Assert.Equal(Rtfq.Contracts.ErrorCodes.SourceUnreachable, ex.ErrorCode);
    }

    static IEnumerable<Exception> Unwrap(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) yield return e;
    }
}
