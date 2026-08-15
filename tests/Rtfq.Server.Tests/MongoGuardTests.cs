using Rtfq.Adapters;
using Rtfq.Adapters.Mongo;
using Rtfq.Contracts;

namespace Rtfq.Server.Tests;

/// <summary>
/// MongoDB's read guard.
///
/// The interesting part is that Mongo has its own version of the ADR 0001
/// finding: <c>$out</c> and <c>$merge</c> are aggregation <i>stages</i> that
/// write a collection, and <c>$where</c>/<c>$function</c> execute server-side
/// JavaScript. None is a write <i>command</i>, so a command-name allow-list —
/// the obvious design — waves every one of them through.
/// </summary>
public class MongoGuardTests
{
    static AdapterException Refused(string command) =>
        Assert.Throws<AdapterException>(() => MongoReadGuard.Prepare(command, 100));

    [Fact]
    public void A_find_is_a_read_and_gains_a_limit()
    {
        var result = MongoReadGuard.Prepare("""{"find": "orders", "filter": {"status": "stuck"}}""", 100);

        Assert.True(result.Rewritten);
        Assert.Equal("orders", result.Collection);
        Assert.Equal(100, result.Command["limit"].AsInt32);
    }

    [Fact]
    public void An_existing_smaller_limit_is_left_alone()
    {
        var result = MongoReadGuard.Prepare("""{"find": "orders", "limit": 5}""", 100);

        Assert.False(result.Rewritten);
        Assert.Equal(5, result.Command["limit"].AsInt32);
    }

    [Fact]
    public void An_existing_larger_limit_is_tightened()
    {
        var result = MongoReadGuard.Prepare("""{"find": "orders", "limit": 10000}""", 100);

        Assert.True(result.Rewritten);
        Assert.Equal(100, result.Command["limit"].AsInt32);
    }

    [Fact]
    public void An_aggregate_gains_a_limit_stage_and_a_cursor()
    {
        var result = MongoReadGuard.Prepare(
            """{"aggregate": "orders", "pipeline": [{"$match": {"status": "stuck"}}]}""", 100);

        Assert.True(result.Rewritten);
        var pipeline = result.Command["pipeline"].AsBsonArray;
        Assert.Equal(100, pipeline[^1].AsBsonDocument["$limit"].AsInt32);
        Assert.True(result.Command.Contains("cursor"));
    }

    // --- writes -------------------------------------------------------------

    [Theory]
    [InlineData("""{"insert": "orders", "documents": [{"a": 1}]}""")]
    [InlineData("""{"update": "orders", "updates": [{"q": {}, "u": {"$set": {"a": 1}}}]}""")]
    [InlineData("""{"delete": "orders", "deletes": [{"q": {}, "limit": 0}]}""")]
    [InlineData("""{"findAndModify": "orders", "query": {}, "update": {"$set": {"a": 1}}}""")]
    public void Write_commands_are_refused(string command) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(command).ErrorCode);

    [Theory]
    [InlineData("""{"drop": "orders"}""")]
    [InlineData("""{"createIndexes": "orders", "indexes": []}""")]
    [InlineData("""{"dropDatabase": 1}""")]
    [InlineData("""{"mapReduce": "orders", "map": "function(){}", "reduce": "function(){}"}""")]
    public void Administrative_commands_are_refused(string command) =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(command).ErrorCode);

    // --- the ones a command allow-list alone would miss --------------------------

    [Fact]
    public void An_aggregate_ending_in_out_writes_a_collection_and_is_refused()
    {
        var ex = Refused("""{"aggregate": "orders", "pipeline": [{"$match": {}}, {"$out": "copy"}]}""");

        Assert.Equal(ErrorCodes.InsufficientAccess, ex.ErrorCode);
        Assert.Contains("$out", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Merge_upserts_into_a_collection_and_is_refused() =>
        Assert.Equal(ErrorCodes.InsufficientAccess,
            Refused("""{"aggregate": "orders", "pipeline": [{"$merge": {"into": "copy"}}]}""").ErrorCode);

    /// <summary>
    /// The exhaustive-walk case: $out nested inside $facet is not the last stage
    /// and not at the top level, so a guard that only inspects the tail misses it.
    /// </summary>
    [Fact]
    public void Out_buried_inside_a_facet_is_still_found() =>
        Assert.Equal(ErrorCodes.InsufficientAccess, Refused(
            """{"aggregate": "orders", "pipeline": [{"$facet": {"a": [{"$out": "copy"}]}}]}""").ErrorCode);

    [Theory]
    [InlineData("""{"find": "orders", "filter": {"$where": "this.total > 100"}}""")]
    [InlineData("""{"aggregate": "orders", "pipeline": [{"$match": {"$expr": {"$function": {"body": "function(){}", "args": [], "lang": "js"}}}}]}""")]
    public void Server_side_javascript_is_refused(string command)
    {
        var ex = Refused(command);
        Assert.Equal(ErrorCodes.InsufficientAccess, ex.ErrorCode);
        Assert.Contains("JavaScript", ex.Message, StringComparison.Ordinal);
    }

    // --- malformed input -----------------------------------------------------------

    [Theory]
    [InlineData("@@@ not a document")]
    [InlineData("SELECT * FROM orders")]   // right idea, wrong dialect
    public void Non_documents_are_refused_with_a_hint_about_the_dialect(string command)
    {
        var ex = Refused(command);
        Assert.Equal(ErrorCodes.SourceRejected, ex.ErrorCode);
        Assert.Contains("find", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The command name is the first field per the wire protocol. Reading any
    /// other field would let a caller put a decoy read in front of a write.
    /// </summary>
    [Fact]
    public void The_command_is_the_first_field_not_any_field() =>
        Assert.Equal(ErrorCodes.InsufficientAccess,
            Refused("""{"delete": "orders", "deletes": [], "find": "orders"}""").ErrorCode);
}
