using Rtfq.Adapters;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Tests.Conformance;

/// <summary>
/// What a fixture must supply for its adapter to be judged by the shared suite.
/// Each value is expressed in the source's <b>native dialect</b>, because that is
/// the one thing the adapters legitimately differ on.
/// </summary>
public interface IAdapterFixture
{
    ISourceAdapter Adapter { get; }

    /// <summary>Qualified name of a seeded table, collection or endpoint.</summary>
    string SampleTarget { get; }

    /// <summary>A valid read over the seeded data, returning more than one row.</summary>
    string ReadStatement { get; }

    /// <summary>A statement the guard must refuse as a write.</summary>
    string WriteStatement { get; }

    /// <summary>Input that is not valid in this dialect at all.</summary>
    string NonsenseStatement { get; }

    /// <summary>How many rows the seed contains, so the cap can be tested against something known.</summary>
    int SeededRows { get; }

    bool SupportsExplain { get; }
    bool SupportsIntrospection { get; }
}

/// <summary>
/// The suite every adapter passes, against a real instance.
///
/// It exists because M2's actual deliverable is not three adapters but the
/// evidence that one interface describes all of them. A behaviour that has to be
/// special-cased per adapter here is a defect in <see cref="ISourceAdapter"/>,
/// not a quirk of an engine — so the shape of this file is the argument.
/// </summary>
public abstract class AdapterConformance<TFixture>(TFixture fixture)
    where TFixture : class, IAdapterFixture
{
    protected TFixture Fixture { get; } = fixture;

    static ReadOptions Options(int maxRows) => new(maxRows, TimeSpan.FromSeconds(15));

    [Fact]
    public async Task Check_reports_reachability_and_capabilities()
    {
        var capabilities = await Fixture.Adapter.CheckAsync(CancellationToken.None);

        Assert.NotNull(capabilities);
        // Nothing may claim transactional DDL without transactional writes: DDL is
        // the stronger promise, so the pair would be incoherent.
        if (capabilities.TransactionalDdl) Assert.True(capabilities.TransactionalWrites);
    }

    [Fact]
    public async Task A_read_returns_columns_and_rows()
    {
        var result = await Fixture.Adapter.ExecuteReadAsync(
            Fixture.ReadStatement, Options(100), CancellationToken.None);

        Assert.NotEmpty(result.Columns);
        Assert.True(result.RowCount > 0, "the seeded read returned nothing");
        Assert.Equal(result.RowCount, result.Rows.Count);
    }

    [Fact]
    public async Task The_row_cap_is_enforced_and_truncation_is_reported()
    {
        var result = await Fixture.Adapter.ExecuteReadAsync(
            Fixture.ReadStatement, Options(2), CancellationToken.None);

        Assert.Equal(2, result.RowCount);
        Assert.True(result.Truncated, "more rows existed than the cap allowed, so truncated must be true");
    }

    [Fact]
    public async Task A_result_that_exactly_fills_the_cap_is_not_truncated()
    {
        var result = await Fixture.Adapter.ExecuteReadAsync(
            Fixture.ReadStatement, Options(Fixture.SeededRows), CancellationToken.None);

        Assert.Equal(Fixture.SeededRows, result.RowCount);
        Assert.False(result.Truncated, "exactly-full is not the same as clipped");
    }

    [Fact]
    public async Task A_write_is_refused_by_the_guard()
    {
        var ex = await Assert.ThrowsAsync<AdapterException>(() => Fixture.Adapter.ExecuteReadAsync(
            Fixture.WriteStatement, Options(10), CancellationToken.None));

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.ErrorCode);
    }

    [Fact]
    public async Task Nonsense_is_refused_rather_than_forwarded()
    {
        var ex = await Assert.ThrowsAsync<AdapterException>(() => Fixture.Adapter.ExecuteReadAsync(
            Fixture.NonsenseStatement, Options(10), CancellationToken.None));

        Assert.Equal(ErrorCodes.SourceRejected, ex.ErrorCode);
    }

    [Fact]
    public async Task Sampling_returns_rows_and_respects_its_own_bound()
    {
        var result = await Fixture.Adapter.SampleAsync(
            Fixture.SampleTarget, 2, CancellationToken.None);

        Assert.True(result.RowCount <= 2);
        Assert.NotEmpty(result.Columns);
    }

    [Fact]
    public async Task Introspection_describes_the_seeded_target()
    {
        if (!Fixture.SupportsIntrospection) return;

        var snapshot = await Fixture.Adapter.IntrospectAsync(CancellationToken.None);

        Assert.NotEmpty(snapshot.Tables);
        Assert.Equal(Fixture.Adapter.Name, snapshot.Source);

        var target = snapshot.Find(Fixture.SampleTarget);
        Assert.NotNull(target);
        Assert.NotEmpty(target!.Columns);
    }

    [Fact]
    public async Task Explain_either_plans_or_refuses_in_the_taxonomy()
    {
        if (Fixture.SupportsExplain)
        {
            var plan = await Fixture.Adapter.ExplainAsync(
                Fixture.ReadStatement, TimeSpan.FromSeconds(15), CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(plan));
        }
        else
        {
            // Not supporting a capability is fine. Failing in some untyped way is not.
            var ex = await Assert.ThrowsAsync<AdapterException>(() => Fixture.Adapter.ExplainAsync(
                Fixture.ReadStatement, TimeSpan.FromSeconds(15), CancellationToken.None));

            Assert.False(string.IsNullOrWhiteSpace(ex.ErrorCode));
        }
    }

    [Fact]
    public async Task Capabilities_render_to_the_wire_without_dialect_leakage()
    {
        var capabilities = await Fixture.Adapter.CheckAsync(CancellationToken.None);
        var wire = capabilities.ToWire();

        Assert.All(wire, c => Assert.Contains(c,
            new[] { "transactional_writes", "transactional_ddl", "explain", "introspection" }, StringComparer.Ordinal));
    }
}
