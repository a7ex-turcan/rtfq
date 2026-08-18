using Rtfq.Client;
using Rtfq.Contracts;
using Rtfq.Mcp;

namespace Rtfq.Adapters.Tests;

[Collection(nameof(RtfqCollection))]
public sealed class DiscoveryTests(RtfqFixture fixture)
{
    // --- describe_source ----------------------------------------------------

    [Fact]
    public async Task Describe_source_lists_tables_with_row_estimates()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.DescribeSourceAsync("orders", pattern: "public.");

        Assert.Equal("postgres", result.Kind);
        Assert.Equal("read", result.EffectiveAccess);

        var orders = Assert.Single(result.Tables, t => t.Name == "public.orders");
        Assert.Equal("table", orders.Kind);
        Assert.Equal(6, orders.Columns);
        // A planner estimate, not a count: close, not exact.
        Assert.InRange(orders.EstimatedRows ?? 0, 200, 300);
    }

    [Fact]
    public async Task Describe_source_caps_the_table_list_and_says_how_to_narrow()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.DescribeSourceAsync("orders");

        Assert.True(result.TableCount > RtfqFixture.WideTables);
        Assert.True(result.Truncated);
        Assert.Equal(80, result.Tables.Count);
        Assert.Contains("pattern", result.Hint!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_pattern_narrows_the_list()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        // entity_15 plus entity_150..159: eleven tables, comfortably under the cap.
        var result = await client.DescribeSourceAsync("orders", pattern: "wide.entity_15");

        Assert.False(result.Truncated);
        Assert.Equal(11, result.TableCount);
        Assert.All(result.Tables, t => Assert.StartsWith("wide.entity_15", t.Name, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_discovery_response_states_how_old_the_schema_is()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.DescribeSourceAsync("orders", pattern: "public.orders");

        Assert.False(result.Schema.Stale);
        Assert.InRange(result.Schema.AgeSeconds, 0, 600);
        Assert.False(string.IsNullOrWhiteSpace(result.Schema.CapturedAt));
    }

    // --- describe_table --------------------------------------------------------

    [Fact]
    public async Task Describe_table_returns_columns_keys_and_indexes()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.DescribeTableAsync("orders", "public.orders");

        Assert.Equal("public.orders", result.Table);
        Assert.Equal("id", Assert.Single(result.PrimaryKey));
        // This source declares read and the token was granted read, so there is no
        // write here to report. WritePathTests covers the case where there is.
        Assert.False(result.Writable);

        var total = Assert.Single(result.Columns, c => c.Name == "total");
        Assert.Equal("numeric(10,2)", total.Type);
        Assert.False(total.Nullable);

        var createdAt = Assert.Single(result.Columns, c => c.Name == "created_at");
        Assert.Contains("now()", createdAt.Default!, StringComparison.Ordinal);

        Assert.Contains(result.Indexes, i => i.Name == "idx_orders_vip" && !i.Primary);

        var fk = Assert.Single(result.ForeignKeys);
        Assert.Equal("public.customers", fk.References);
        Assert.Equal("customer_id", Assert.Single(fk.Columns));
    }

    [Fact]
    public async Task An_unqualified_table_name_resolves_when_unambiguous()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.DescribeTableAsync("orders", "customers");

        Assert.Equal("public.customers", result.Table);
    }

    [Fact]
    public async Task A_missing_table_says_how_to_find_the_right_one()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.DescribeTableAsync("orders", "public.nope"));

        Assert.Equal(ErrorCodes.SourceUnknown, ex.Code);
        Assert.Contains("describe_source", ex.Message, StringComparison.Ordinal);
    }

    // --- sample and explain -------------------------------------------------------

    [Fact]
    public async Task Sample_returns_a_few_rows()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.SampleAsync("orders", "public.orders", rows: 3);

        Assert.Equal(3, result.RowCount);
        Assert.Contains(result.Columns, c => c.Name == "customer");
    }

    [Fact]
    public async Task Sample_is_capped_however_much_is_asked_for()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.SampleAsync("orders", "public.orders", rows: 10_000);

        Assert.True(result.RowCount <= 100);
    }

    [Fact]
    public async Task Explain_returns_a_plan_without_running_the_query()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.ExplainAsync("orders", "SELECT * FROM orders WHERE vip");

        Assert.Contains("cost=", result.Plan, StringComparison.Ordinal);
        // A plan, not a result: EXPLAIN without ANALYZE never executes.
        Assert.DoesNotContain("actual time", result.Plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Explain_analyze_is_refused_because_it_executes()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(
            () => client.ExplainAsync("orders", "EXPLAIN ANALYZE DELETE FROM orders"));

        // Refused for the ANALYZE specifically, not for the DELETE it wraps: the
        // executing part is what the caller needs told about.
        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);
        Assert.Contains("ANALYZE", ex.Message, StringComparison.Ordinal);
    }

    // --- the hole this milestone closes ----------------------------------------------

    [Theory]
    [InlineData("UPDATE orders SET vip = true WHERE id = 1")]
    [InlineData("DELETE FROM orders WHERE id = 1")]
    [InlineData("DROP TABLE orders")]
    [InlineData("WITH gone AS (DELETE FROM orders WHERE id = 1 RETURNING *) SELECT * FROM gone")]
    public async Task A_read_token_cannot_write_through_query(string statement)
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var ex = await Assert.ThrowsAsync<RtfqClientException>(() => client.QueryAsync("orders", statement));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.Code);

        // And the table is untouched.
        var count = await client.QueryAsync("orders", "SELECT count(*) FROM orders");
        Assert.Equal(RtfqFixture.SeededRows, count.Rows[0]!.AsArray()[0]!.GetValue<long>());
    }

    // --- limit injection -----------------------------------------------------------------

    [Fact]
    public async Task A_query_with_no_limit_gets_one_and_reports_truncation()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var result = await client.QueryAsync("orders", "SELECT * FROM orders");

        Assert.Equal(RtfqFixture.SourceMaxRows, result.RowCount);
        Assert.True(result.Truncated);
        Assert.NotNull(result.Hint);
        Assert.Contains("no pagination", result.Hint!, StringComparison.Ordinal);
        Assert.Null(result.NextCursor);
    }

    // --- the token budget --------------------------------------------------------------------

    /// <summary>
    /// The go/no-go property. Discovery output is paid for in the agent's context
    /// on every call, so it is bounded and the bound is asserted — otherwise it
    /// regresses silently the first time someone adds a field.
    ///
    /// Characters divided by four is the usual rough token estimate; the ceilings
    /// below are deliberately generous so this fails on a design regression rather
    /// than on a word.
    /// </summary>
    [Fact]
    public async Task Discovery_output_stays_inside_its_token_budget()
    {
        using var client = fixture.Client(RtfqFixture.GrantedToken);

        var source = Render.Source(await client.DescribeSourceAsync("orders"));
        var table = Render.Table(await client.DescribeTableAsync("orders", "public.orders"));
        var sources = Render.Sources(await client.ListSourcesAsync());

        var sourceTokens = source.Length / 4;
        var tableTokens = table.Length / 4;

        // Printed, not just asserted: a ceiling tells you when compactness broke,
        // a number tells you when it started slipping.
        Console.WriteLine($"token budget: list_sources ~{sources.Length / 4}, " +
                          $"describe_source ~{sourceTokens} ({RtfqFixture.WideTables + 2} tables), " +
                          $"describe_table ~{tableTokens}");

        Assert.True(sourceTokens < 1500,
            $"describe_source on a {RtfqFixture.WideTables}-table database rendered ~{sourceTokens} tokens:\n{source}");
        Assert.True(tableTokens < 400, $"describe_table rendered ~{tableTokens} tokens:\n{table}");
        Assert.True(sources.Length / 4 < 100, $"list_sources rendered ~{sources.Length / 4} tokens");

        // Compactness must not have been bought by dropping what an agent needs.
        // Tables are grouped under a schema header rather than repeating the
        // qualified prefix on all two hundred lines, so assert the grouped form.
        Assert.Contains("[public]", source, StringComparison.Ordinal);
        Assert.Contains("orders", source, StringComparison.Ordinal);
        Assert.Contains("public.orders", table, StringComparison.Ordinal);
        Assert.Contains("customer_id -> public.customers(id)", table, StringComparison.Ordinal);
        Assert.Contains("pk", table, StringComparison.Ordinal);
        Assert.Contains("idx_orders_vip", table, StringComparison.Ordinal);
    }
}
