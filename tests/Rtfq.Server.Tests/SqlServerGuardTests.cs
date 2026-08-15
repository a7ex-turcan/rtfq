using Rtfq.Adapters;
using Rtfq.Adapters.SqlServer;
using Rtfq.Contracts;

namespace Rtfq.Server.Tests;

/// <summary>
/// The T-SQL read guard.
///
/// Seeded from the corpus in <c>spike/parser</c>. The cases that differ from
/// PostgreSQL are the ones worth having: T-SQL allows several statements in a
/// batch with no separator at all, <c>GO</c> splits batches client-side, and
/// <c>EXEC</c> can carry SQL as a string the parse tree cannot see into.
/// </summary>
public class SqlServerGuardTests
{
    static AdapterException Refused(string sql) =>
        Assert.Throws<AdapterException>(() => SqlServerReadGuard.Prepare(sql, 100));

    [Fact]
    public void A_plain_select_passes_and_gains_a_top()
    {
        var result = SqlServerReadGuard.Prepare("SELECT id FROM widgets", 100);

        Assert.True(result.Rewritten);
        Assert.Contains("TOP", result.Statement, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("100", result.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_smaller_top_is_left_alone()
    {
        var result = SqlServerReadGuard.Prepare("SELECT TOP (5) id FROM widgets", 100);

        Assert.False(result.Rewritten);
    }

    [Fact]
    public void An_existing_larger_top_is_tightened()
    {
        var result = SqlServerReadGuard.Prepare("SELECT TOP (10000) id FROM widgets", 100);

        Assert.True(result.Rewritten);
        Assert.DoesNotContain("10000", result.Statement, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rewritten_statement_is_stable_when_prepared_again()
    {
        var once = SqlServerReadGuard.Prepare("SELECT id FROM widgets", 100);
        var twice = SqlServerReadGuard.Prepare(once.Statement, 100);

        Assert.False(twice.Rewritten);
    }

    // --- writes and DDL ------------------------------------------------------

    [Theory]
    [InlineData("UPDATE widgets SET name = 'x' WHERE id = 1")]
    [InlineData("DELETE FROM widgets WHERE id = 1")]
    [InlineData("INSERT INTO widgets (id) VALUES (1)")]
    // T-SQL requires MERGE to be semicolon-terminated; without it ScriptDom
    // reports a parse error, which is a different (also correct) refusal.
    [InlineData("MERGE widgets AS t USING staging AS s ON t.id = s.id WHEN MATCHED THEN UPDATE SET name = s.name;")]
    public void Writes_are_refused(string sql) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(sql).ErrorCode);

    [Theory]
    [InlineData("DROP TABLE widgets")]
    [InlineData("TRUNCATE TABLE widgets")]
    [InlineData("ALTER TABLE widgets ADD extra int")]
    [InlineData("CREATE INDEX ix ON widgets (id)")]
    public void Ddl_is_refused(string sql) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(sql).ErrorCode);

    /// <summary>EXEC can carry SQL as a string literal, which no parse tree can see into.</summary>
    [Theory]
    [InlineData("EXEC('DELETE FROM widgets')")]
    [InlineData("EXEC sp_executesql N'DELETE FROM widgets'")]
    [InlineData("EXEC xp_cmdshell 'dir'")]
    public void Exec_is_refused_because_the_payload_is_opaque(string sql)
    {
        var ex = Refused(sql);
        Assert.Equal(ErrorCodes.InsufficientAccess, ex.ErrorCode);
        Assert.Contains("dynamic SQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Select_into_creates_a_table_and_is_refused() =>
        Assert.Equal(ErrorCodes.InsufficientAccess,
            Refused("SELECT * INTO archived FROM widgets").ErrorCode);

    // --- dialect-specific smuggling ---------------------------------------------

    [Fact]
    public void Stacked_statements_are_refused() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("SELECT 1; DROP TABLE widgets").ErrorCode);

    /// <summary>T-SQL needs no separator at all, which PostgreSQL does not permit.</summary>
    [Fact]
    public void A_batch_with_no_separator_is_still_two_statements() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("SELECT 1 DROP TABLE widgets").ErrorCode);

    [Fact]
    public void Go_splits_batches_and_is_refused() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("SELECT 1\nGO\nDROP TABLE widgets").ErrorCode);

    [Fact]
    public void Nonsense_is_refused_rather_than_forwarded() =>
        Assert.Equal(ErrorCodes.SourceRejected, Refused("@@@ not sql at all").ErrorCode);

    [Fact]
    public void Explain_validation_does_not_add_a_top()
    {
        var result = SqlServerReadGuard.Prepare("SELECT id FROM widgets", maxRows: null);

        Assert.False(result.Rewritten);
        Assert.DoesNotContain("TOP", result.Statement, StringComparison.OrdinalIgnoreCase);
    }
}
