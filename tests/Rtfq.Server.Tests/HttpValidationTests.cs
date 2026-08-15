using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Tests;

/// <summary>
/// Config rules specific to HTTP sources.
///
/// The one CLAUDE.md names outright: a wildcard path combined with a write method
/// is an ERROR, not a warning. Each half is harmless; together they hand over the
/// whole API, and a config review that sees them on separate lines will miss it.
/// </summary>
public class HttpValidationTests
{
    static RtfqConfig ConfigWith(SourceSection source) => new()
    {
        Server = new ServerSection
        {
            Listen = "127.0.0.1:7420",
            Auth = new AuthSection
            {
                Mode = "token",
                Tokens =
                [
                    new TokenSection
                    {
                        Id = "agent", Secret = "s", SecretWasReference = true,
                        Grants = new Dictionary<string, AccessLevel> { [source.Name] = AccessLevel.Read },
                    },
                ],
            },
        },
        Defaults = new DefaultsSection(),
        Sources = [source],
    };

    static SourceSection Http(
        IReadOnlyList<string>? methods = null,
        IReadOnlyList<string>? paths = null,
        AccessLevel access = AccessLevel.Read,
        string baseUrl = "https://billing.internal",
        bool inlineHeader = false) => new()
        {
            Name = "billing",
            Kind = "http",
            Dsn = "",
            DsnWasReference = true,
            BaseUrl = baseUrl,
            Methods = methods ?? ["GET"],
            AllowPaths = paths ?? ["/v1/invoices"],
            Access = access,
            HeadersHadInlineSecret = inlineHeader,
        };

    static ValidationResult Validate(SourceSection source, bool production = false) =>
        ConfigValidator.Validate(ConfigWith(source), production);

    [Fact]
    public void A_read_only_http_source_with_explicit_paths_is_valid()
    {
        var result = Validate(Http());

        Assert.False(result.HasErrors, string.Join("; ", result.Errors));
    }

    [Fact]
    public void A_wildcard_path_with_a_write_method_is_an_error()
    {
        var result = Validate(Http(methods: ["GET", "POST"], paths: ["/v1/invoices/*"]));

        var error = Assert.Single(result.Errors, d => d.Check == "http.wildcard_write");
        Assert.Contains("hands over the whole API", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_wildcard_path_with_only_reads_is_fine()
    {
        var result = Validate(Http(methods: ["GET"], paths: ["/v1/invoices/*"]));

        Assert.DoesNotContain(result.Errors, d => d.Check == "http.wildcard_write");
    }

    [Fact]
    public void An_explicit_path_with_a_write_method_is_fine()
    {
        // The rule is about the combination, not about writes being forbidden.
        var result = Validate(Http(methods: ["GET", "POST"], paths: ["/v1/invoices"]));

        Assert.DoesNotContain(result.Errors, d => d.Check == "http.wildcard_write");
    }

    [Fact]
    public void An_empty_allow_list_reaches_nothing_and_is_an_error()
    {
        var result = Validate(Http(paths: []));

        Assert.Contains(result.Errors, d => d.Check == "http.allow_paths_present");
    }

    [Theory]
    [InlineData("/v1/*/invoices")]   // wildcard in the middle reads narrow, matches broad
    [InlineData("/v1/*/*")]
    public void A_wildcard_anywhere_but_the_end_is_refused(string path) =>
        Assert.Contains(Validate(Http(paths: [path])).Errors, d => d.Check == "http.wildcard_suffix_only");

    [Fact]
    public void An_unrooted_path_is_refused() =>
        Assert.Contains(Validate(Http(paths: ["v1/invoices"])).Errors, d => d.Check == "http.allow_path_rooted");

    /// <summary>An HTTP source has no transaction to roll back, so it can never be writable.</summary>
    [Fact]
    public void An_http_source_may_not_be_writable() =>
        Assert.Contains(Validate(Http(access: AccessLevel.Write)).Errors,
            d => d.Check == "source.writes_require_transactions");

    [Fact]
    public void Plain_http_is_allowed_in_development_and_refused_in_production()
    {
        var source = Http(baseUrl: "http://billing.internal");

        Assert.DoesNotContain(Validate(source).Errors, d => d.Check == "http.tls_in_production");
        Assert.Contains(Validate(source, production: true).Errors, d => d.Check == "http.tls_in_production");
    }

    [Fact]
    public void An_inline_header_secret_warns_in_development_and_fails_in_production()
    {
        var source = Http(inlineHeader: true);

        Assert.Contains(Validate(source).Warnings, d => d.Check == "http.header_not_inline");
        Assert.Contains(Validate(source, production: true).Errors, d => d.Check == "http.header_not_inline");
    }

    [Fact]
    public void A_mongo_source_may_not_declare_access_schema() =>
        Assert.Contains(Validate(new SourceSection
        {
            Name = "billing", Kind = "mongodb", Dsn = "mongodb://x", DsnWasReference = true,
            Access = AccessLevel.Schema,
        }).Errors, d => d.Check == "source.schema_requires_transactional_ddl");

    /// <summary>
    /// Mongo writes are NOT rejected offline: a replica set can do them, and only
    /// connecting reveals the topology. That check belongs at startup.
    /// </summary>
    [Fact]
    public void A_mongo_source_may_declare_access_write_offline() =>
        Assert.DoesNotContain(Validate(new SourceSection
        {
            Name = "billing", Kind = "mongodb", Dsn = "mongodb://x", DsnWasReference = true,
            Access = AccessLevel.Write,
        }).Errors, d => d.Check == "source.writes_require_transactions");
}
