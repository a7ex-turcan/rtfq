using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Tests;

public class ValidationTests
{
    static RtfqConfig Build(string listen, TlsSection? tls, bool secretIsReference, string dsn, bool dsnIsReference,
        AccessLevel access = AccessLevel.Read, string kind = "postgres") =>
        new()
        {
            Server = new ServerSection
            {
                Listen = listen,
                Tls = tls,
                Auth = new AuthSection
                {
                    Mode = "token",
                    Tokens =
                    [
                        new TokenSection
                        {
                            Id = "agent",
                            Secret = "s3cret",
                            SecretWasReference = secretIsReference,
                            Grants = new Dictionary<string, AccessLevel> { ["orders"] = AccessLevel.Read },
                        },
                    ],
                },
            },
            Defaults = new DefaultsSection(),
            Sources =
            [
                new SourceSection
                {
                    Name = "orders", Kind = kind, Dsn = dsn, DsnWasReference = dsnIsReference, Access = access,
                },
            ],
        };

    [Fact]
    public void Tls_is_required_once_the_listener_leaves_this_machine()
    {
        var config = Build("0.0.0.0:7420", tls: null, secretIsReference: true, "Host=db", dsnIsReference: true);

        var result = ConfigValidator.Validate(config, production: false);

        Assert.Contains(result.Errors, d => d.Check == "server.tls.required_unless_loopback");
    }

    [Fact]
    public void Loopback_without_tls_is_fine_in_development()
    {
        var config = Build("127.0.0.1:7420", tls: null, secretIsReference: true, "Host=db", dsnIsReference: true);

        var result = ConfigValidator.Validate(config, production: false);

        Assert.DoesNotContain(result.Errors, d => d.Check.StartsWith("server.tls", StringComparison.Ordinal));
    }

    // The M0 exit criterion: the same config warns in dev and fails in production.
    [Fact]
    public void Inline_token_secret_warns_in_development()
    {
        var config = Build("127.0.0.1:7420", null, secretIsReference: false, "Host=db", dsnIsReference: true);

        var result = ConfigValidator.Validate(config, production: false);

        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
        Assert.Contains(result.Warnings, d => d.Check == "auth.token.secret_not_inline");
    }

    [Fact]
    public void Inline_token_secret_is_fatal_in_production()
    {
        var config = Build("127.0.0.1:7420", null, secretIsReference: false, "Host=db", dsnIsReference: true);

        var result = ConfigValidator.Validate(config, production: true);

        Assert.Contains(result.Errors, d => d.Check == "auth.token.secret_not_inline");
    }

    [Fact]
    public void Inline_dsn_password_is_fatal_in_production()
    {
        var config = Build("127.0.0.1:7420", null, secretIsReference: true, "Host=db;Password=hunter2", dsnIsReference: false);

        var result = ConfigValidator.Validate(config, production: true);

        Assert.Contains(result.Errors, d => d.Check == "source.dsn_not_inline");
    }

    [Fact]
    public void A_grant_naming_an_undeclared_source_is_an_error()
    {
        var config = Build("127.0.0.1:7420", null, true, "Host=db", true);
        config = config with { Sources = [] };

        var result = ConfigValidator.Validate(config, production: false);

        Assert.Contains(result.Errors, d => d.Check == "auth.grant.source_exists");
    }

    [Fact]
    public void Serving_with_no_tokens_is_refused()
    {
        var config = Build("127.0.0.1:7420", null, true, "Host=db", true);
        config = config with
        {
            Server = config.Server with { Auth = new AuthSection { Mode = "token", Tokens = [] } },
        };

        var result = ConfigValidator.Validate(config, production: false);

        Assert.Contains(result.Errors, d => d.Check == "auth.tokens_present");
    }

    // ADR 0002: registered in M0, fires the moment a non-transactional kind exists.
    [Fact]
    public void A_kind_without_transactional_ddl_may_not_declare_access_schema()
    {
        var config = Build("127.0.0.1:7420", null, true, "mongodb://db", true, AccessLevel.Schema, kind: "mongodb");

        var result = ConfigValidator.Validate(config, production: false);

        Assert.Contains(result.Errors, d => d.Check == "source.schema_requires_transactional_ddl");
    }

    [Theory]
    [InlineData("127.0.0.1:7420", true)]
    [InlineData("localhost:7420", true)]
    [InlineData("0.0.0.0:7420", true)]
    [InlineData("127.0.0.1:0", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.0.0.1:99999", false)]
    [InlineData("db.internal:7420", false)]
    public void Listen_addresses_are_parsed_or_refused(string listen, bool valid) =>
        Assert.Equal(valid, ConfigValidator.TryParseListen(listen, out _));

    // --- the one place a path is wanted rather than a reference ----------------

    /// <summary>
    /// Every other secret in the config is referenced rather than inlined, so
    /// reaching for ${file:...} here is the natural mistake — and it substitutes
    /// the PEM itself. The diagnostic has to name that mistake without echoing
    /// what it was handed, because what it was handed is a private key and the
    /// error lands in a terminal, a CI log, or a screenshot.
    /// </summary>
    [Fact]
    public void A_tls_key_pasted_in_place_of_its_path_is_named_and_never_echoed()
    {
        const string pem = """
            -----BEGIN PRIVATE KEY-----
            MIIEvQIBADANBgkqhkiG9w0BAQEFAASCBKcwggSjAgEAAoIBAQDeadbeefdeadbeef
            -----END PRIVATE KEY-----
            """;

        var result = ConfigValidator.Validate(
            Build("127.0.0.1:7420", new TlsSection { CertPath = "/etc/rtfq/tls.crt", KeyPath = pem },
                secretIsReference: true, dsn: "Host=localhost", dsnIsReference: true),
            production: false);

        var diagnostic = Assert.Single(result.Diagnostics, d => d.Check == "server.tls.path_not_contents");
        Assert.Equal(Severity.Error, diagnostic.Severity);
        Assert.Contains("takes a path", diagnostic.Message);

        // The material itself must appear nowhere in what we print.
        Assert.DoesNotContain("BEGIN PRIVATE KEY", diagnostic.Message);
        Assert.DoesNotContain("deadbeef", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_tls_path_that_is_merely_wrong_still_says_which_path()
    {
        var result = ConfigValidator.Validate(
            Build("127.0.0.1:7420", new TlsSection { CertPath = "/etc/rtfq/nope.crt", KeyPath = "/etc/rtfq/nope.key" },
                secretIsReference: true, dsn: "Host=localhost", dsnIsReference: true),
            production: false);

        // A typo in a filename is the common case, and it is unhelpful to hide it.
        Assert.Contains(result.Diagnostics,
            d => d.Check == "server.tls.files_exist" && d.Message.Contains("/etc/rtfq/nope.crt"));
    }

    [Fact]
    public void An_absurdly_long_tls_path_is_truncated_rather_than_dumped()
    {
        var long_ = "/etc/rtfq/" + new string('x', 500) + ".crt";

        var result = ConfigValidator.Validate(
            Build("127.0.0.1:7420", new TlsSection { CertPath = long_, KeyPath = "/etc/rtfq/tls.key" },
                secretIsReference: true, dsn: "Host=localhost", dsnIsReference: true),
            production: false);

        var diagnostic = Assert.Single(result.Diagnostics,
            d => d.Check == "server.tls.files_exist" && d.Message.Contains("certificate"));
        Assert.True(diagnostic.Message.Length < 200, $"diagnostic was {diagnostic.Message.Length} characters");
    }
}
