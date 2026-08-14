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
}
