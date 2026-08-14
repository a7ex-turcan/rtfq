using Rtfq.Contracts;
using Rtfq.Server.Auth;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;

namespace Rtfq.Server.Tests;

public class PolicyTests
{
    static RtfqConfig ConfigWith(AccessLevel sourceAccess, AccessLevel? grant) => new()
    {
        Server = new ServerSection
        {
            Listen = "127.0.0.1:0",
            Auth = new AuthSection
            {
                Mode = "token",
                Tokens =
                [
                    new TokenSection
                    {
                        Id = "agent",
                        Secret = "s3cret",
                        SecretWasReference = true,
                        Grants = grant is null
                            ? new Dictionary<string, AccessLevel>()
                            : new Dictionary<string, AccessLevel> { ["orders"] = grant.Value },
                    },
                ],
            },
        },
        Defaults = new DefaultsSection(),
        Sources =
        [
            new SourceSection { Name = "orders", Kind = "postgres", Dsn = "Host=db", DsnWasReference = true, Access = sourceAccess },
        ],
    };

    static Caller CallerWith(AccessLevel? grant) => new("agent",
        grant is null ? new Dictionary<string, AccessLevel>() : new Dictionary<string, AccessLevel> { ["orders"] = grant.Value });

    [Theory]
    // source access,        token grant,          asking for,          allowed?
    [InlineData(AccessLevel.Read, AccessLevel.Read, AccessLevel.Read, true)]
    [InlineData(AccessLevel.Write, AccessLevel.Read, AccessLevel.Read, true)]
    [InlineData(AccessLevel.Read, AccessLevel.Write, AccessLevel.Read, true)]
    // A writable source reached by a read-only token is read-only.
    [InlineData(AccessLevel.Write, AccessLevel.Read, AccessLevel.Write, false)]
    // A write-granted token pointed at a read-only source gets nothing extra.
    [InlineData(AccessLevel.Read, AccessLevel.Write, AccessLevel.Write, false)]
    [InlineData(AccessLevel.Write, AccessLevel.Write, AccessLevel.Write, true)]
    [InlineData(AccessLevel.Write, AccessLevel.Schema, AccessLevel.Schema, false)]
    [InlineData(AccessLevel.Schema, AccessLevel.Write, AccessLevel.Schema, false)]
    [InlineData(AccessLevel.Schema, AccessLevel.Schema, AccessLevel.Schema, true)]
    public void Effective_permission_is_the_intersection(
        AccessLevel sourceAccess, AccessLevel grant, AccessLevel required, bool allowed)
    {
        var engine = new PolicyEngine(ConfigWith(sourceAccess, grant));

        var decision = engine.Evaluate(CallerWith(grant), "orders", required);

        Assert.Equal(allowed, decision.Allowed);
        if (!allowed) Assert.Equal(ErrorCodes.InsufficientAccess, decision.ErrorCode);
    }

    [Fact]
    public void A_source_with_no_grant_is_indistinguishable_from_one_that_does_not_exist()
    {
        // Ask for the SAME name in both worlds: one where 'orders' exists but this
        // caller holds no grant, one where it was never declared. If the two answers
        // differ, an unauthorised caller can enumerate which sources exist.
        var exists = new PolicyEngine(ConfigWith(AccessLevel.Write, grant: null));

        var withoutSource = ConfigWith(AccessLevel.Write, grant: null) with { Sources = [] };
        var absent = new PolicyEngine(withoutSource);

        var ungranted = exists.Evaluate(CallerWith(null), "orders", AccessLevel.Read);
        var nonexistent = absent.Evaluate(CallerWith(null), "orders", AccessLevel.Read);

        Assert.Equal(ErrorCodes.SourceUnknown, ungranted.ErrorCode);
        Assert.Equal(ErrorCodes.SourceUnknown, nonexistent.ErrorCode);
        Assert.Equal(ungranted.Message, nonexistent.Message);
        Assert.Equal(ungranted.Outcome, nonexistent.Outcome);
    }

    [Fact]
    public void Sources_the_caller_cannot_reach_are_not_listed()
    {
        var engine = new PolicyEngine(ConfigWith(AccessLevel.Write, grant: null));

        Assert.Empty(engine.VisibleSources(CallerWith(null)));
    }

    [Fact]
    public void Listed_sources_report_the_intersected_access_not_the_declared_one()
    {
        var engine = new PolicyEngine(ConfigWith(AccessLevel.Schema, AccessLevel.Read));

        var (source, effective) = Assert.Single(engine.VisibleSources(CallerWith(AccessLevel.Read)));

        Assert.Equal(AccessLevel.Schema, source.Access);
        Assert.Equal(AccessLevel.Read, effective);
    }
}

public class AuthTests
{
    static TokenAuthenticator Authenticator() => new(new RtfqConfig
    {
        Server = new ServerSection
        {
            Listen = "127.0.0.1:0",
            Auth = new AuthSection
            {
                Mode = "token",
                Tokens =
                [
                    new TokenSection { Id = "a", Secret = "alpha-secret", SecretWasReference = true, Grants = new Dictionary<string, AccessLevel>() },
                    new TokenSection { Id = "b", Secret = "beta-secret", SecretWasReference = true, Grants = new Dictionary<string, AccessLevel>() },
                ],
            },
        },
        Defaults = new DefaultsSection(),
        Sources = [],
    });

    [Theory]
    [InlineData("alpha-secret", "a")]
    [InlineData("beta-secret", "b")]
    public void A_valid_token_identifies_its_caller(string secret, string expectedId) =>
        Assert.Equal(expectedId, Authenticator().Authenticate(secret)?.TokenId);

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("wrong")]
    [InlineData("alpha-secre")]
    [InlineData("alpha-secret ")]
    public void Anything_else_authenticates_as_nobody(string? presented) =>
        Assert.Null(Authenticator().Authenticate(presented));

    [Theory]
    [InlineData("Bearer abc", "abc")]
    [InlineData("bearer abc", "abc")]
    [InlineData("Bearer  abc  ", "abc")]
    [InlineData("Basic abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Bearer_extraction(string? header, string? expected) =>
        Assert.Equal(expected, TokenAuthenticator.ExtractBearer(header));
}
