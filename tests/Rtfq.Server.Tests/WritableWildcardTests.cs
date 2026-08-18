using Rtfq.Contracts;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;

namespace Rtfq.Server.Tests;

/// <summary>
/// Gate three, once the allow-list learned patterns (ADR 0008).
///
/// The question every test here asks is the same one: can a pattern be made to
/// cover something the person who wrote it would not have agreed to? Widening a
/// gate is only safe if the widening is exactly as wide as it reads.
/// </summary>
public sealed class WritableWildcardTests
{
    static SourceSection Source(string[] writable, string[]? deny = null) => new()
    {
        Name = "db",
        Kind = "mssql",
        Dsn = "Server=x",
        DsnWasReference = true,
        Access = AccessLevel.Write,
        WritableTables = writable,
        DenyTables = deny ?? [],
    };

    static TargetOutcome Write(string[] writable, string target, string[]? deny = null) =>
        TargetPolicy.EvaluateWrite(Source(writable, deny), target);

    // --- the feature ------------------------------------------------------------

    [Fact]
    public void A_schema_pattern_covers_tables_in_that_schema()
    {
        Assert.Equal(TargetOutcome.Allowed, Write(["dbo.*"], "dbo.orders"));
        Assert.Equal(TargetOutcome.Allowed, Write(["dbo.*"], "dbo.customers"));
    }

    [Fact]
    public void A_schema_pattern_stops_at_its_own_schema()
    {
        // The whole value of writing dbo.* rather than * is that it means dbo.
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.*"], "audit.orders"));
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.*"], "sys.objects"));
    }

    [Fact]
    public void A_pattern_does_not_match_a_schema_that_merely_starts_the_same_way()
    {
        // 'dbo.' is a prefix of 'dbo_secret.' only if the dot is treated loosely.
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.*"], "dbo_secret.keys"));
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.*"], "xdbo.orders"));
    }

    [Fact]
    public void A_bare_star_covers_everything_because_that_is_what_it_says()
    {
        Assert.Equal(TargetOutcome.Allowed, Write(["*"], "dbo.orders"));
        Assert.Equal(TargetOutcome.Allowed, Write(["*"], "anything.at.all"));
    }

    [Fact]
    public void A_star_spans_dots_so_a_two_part_pattern_also_matches_three_parts()
    {
        // Documented rather than desired. The guards emit schema.table, so this
        // is unreachable in practice - but it is a property of the matcher, and
        // an untested property is one that changes without anybody noticing.
        Assert.Equal(TargetOutcome.Allowed, Write(["dbo.*"], "dbo.other.orders"));
    }

    // --- what must not have changed ----------------------------------------------

    [Fact]
    public void An_exact_entry_still_means_exactly_that()
    {
        Assert.Equal(TargetOutcome.Allowed, Write(["dbo.orders"], "dbo.orders"));
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.orders"], "dbo.orders_archive"));
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.orders"], "dbo.order"));
    }

    [Fact]
    public void An_absent_allow_list_is_still_absent_rather_than_permissive()
    {
        Assert.Equal(TargetOutcome.NotWritable, Write([], "dbo.orders"));
    }

    [Fact]
    public void Matching_is_still_case_sensitive()
    {
        // Folding case would be a gate bypass on PostgreSQL, where Orders and
        // orders are genuinely different tables (ADR 0001).
        Assert.Equal(TargetOutcome.NotWritable, Write(["DBO.*"], "dbo.orders"));
        Assert.Equal(TargetOutcome.NotWritable, Write(["dbo.*"], "DBO.orders"));
    }

    // --- deny still wins -----------------------------------------------------------

    [Fact]
    public void Deny_beats_the_broadest_possible_allow()
    {
        // The reason a wildcard is defensible at all: there is still a way to
        // carve something out of it, and that way is evaluated first.
        Assert.Equal(TargetOutcome.Denied, Write(["*"], "dbo.payment_tokens", deny: ["*.payment_tokens"]));
        Assert.Equal(TargetOutcome.Denied, Write(["dbo.*"], "dbo.payment_tokens", deny: ["dbo.payment_tokens"]));
    }

    [Fact]
    public void Deny_wins_for_a_table_matching_both_lists_by_pattern()
    {
        Assert.Equal(TargetOutcome.Denied, Write(["dbo.*"], "dbo.pii_people", deny: ["*.pii_*"]));
    }

    [Fact]
    public void A_carve_out_does_not_deny_its_neighbours()
    {
        Assert.Equal(TargetOutcome.Allowed, Write(["dbo.*"], "dbo.orders", deny: ["*.pii_*"]));
    }
}

public sealed class WritableWildcardValidationTests
{
    const string Base = """
        server:
          listen: 127.0.0.1:7420
          auth:
            mode: token
            tokens:
              - id: agent
                secret: ${env:RTFQ_WILDCARD_TEST}
                grants:
                  db: write
        sources:
          - name: db
            kind: mssql
            dsn: ${env:RTFQ_WILDCARD_DSN}
            access: write
        """;

    static ValidationResult Check(string writableBlock)
    {
        Environment.SetEnvironmentVariable("RTFQ_WILDCARD_TEST", "t");
        Environment.SetEnvironmentVariable("RTFQ_WILDCARD_DSN", "Server=x");
        var load = ConfigLoader.LoadText(Base + Environment.NewLine + writableBlock);
        Assert.NotNull(load.Config);
        return ConfigValidator.Validate(load.Config!, production: false);
    }

    [Fact]
    public void A_wildcard_in_the_allow_list_is_said_out_loud()
    {
        // Not an error - it is a legitimate choice. But a gate that got wider
        // should not do so quietly.
        var result = Check("""
                writable_tables:
                  - dbo.*
            """);

        var d = Assert.Single(result.Diagnostics, x => x.Check == "source.writable_wildcard");
        Assert.Equal(Severity.Warning, d.Severity);
        Assert.Contains("created later", d.Message);
    }

    [Fact]
    public void An_exact_allow_list_says_nothing()
    {
        var result = Check("""
                writable_tables:
                  - dbo.orders
            """);

        Assert.DoesNotContain(result.Diagnostics, x => x.Check == "source.writable_wildcard");
    }

    [Fact]
    public void Each_pattern_is_reported_separately()
    {
        var result = Check("""
                writable_tables:
                  - dbo.*
                  - staging.*
                  - audit.entries
            """);

        Assert.Equal(2, result.Diagnostics.Count(x => x.Check == "source.writable_wildcard"));
    }
}
