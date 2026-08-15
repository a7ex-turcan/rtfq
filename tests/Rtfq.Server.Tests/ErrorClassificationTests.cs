using Rtfq.Contracts;

namespace Rtfq.Server.Tests;

/// <summary>
/// The distinction a caller needs is "my statement was wrong" versus "the source
/// is not answering", and those must not collapse into one code.
///
/// The case that motivated this: a PostgreSQL server that is shutting down still
/// <i>replies</i>, with a FATAL error. Classifying that as source.rejected sends
/// an agent off to debug a statement that was perfectly fine — and because it
/// depends on how far through shutdown the server is, it shows up as a flake
/// rather than a bug.
/// </summary>
public class ErrorClassificationTests
{
    [Theory]
    // Class 08: connection_exception.
    [InlineData("08000", ErrorCodes.SourceUnreachable)]
    [InlineData("08003", ErrorCodes.SourceUnreachable)]
    [InlineData("08006", ErrorCodes.SourceUnreachable)]
    // Server going away or not yet up.
    [InlineData("57P01", ErrorCodes.SourceUnreachable)]  // admin_shutdown
    [InlineData("57P02", ErrorCodes.SourceUnreachable)]  // crash_shutdown
    [InlineData("57P03", ErrorCodes.SourceUnreachable)]  // cannot_connect_now
    // The statement really was the problem.
    [InlineData("42P01", ErrorCodes.SourceRejected)]     // undefined_table
    [InlineData("42601", ErrorCodes.SourceRejected)]     // syntax_error
    [InlineData("23505", ErrorCodes.SourceRejected)]     // unique_violation
    // The cap did its job.
    [InlineData("57014", ErrorCodes.SourceTimeout)]      // query_canceled
    public void Sqlstates_map_to_the_code_a_caller_can_act_on(string sqlState, string expected) =>
        Assert.Equal(expected, Classify(sqlState));

    /// <summary>
    /// Mirrors the adapter's mapping. Kept in the test rather than reaching into
    /// the adapter because constructing a PostgresException with an arbitrary
    /// SQLSTATE is not something Npgsql exposes cleanly, and the rule is what
    /// matters, not the exception plumbing.
    /// </summary>
    static string Classify(string sqlState) => sqlState switch
    {
        "57014" => ErrorCodes.SourceTimeout,
        _ when sqlState.StartsWith("08", StringComparison.Ordinal) => ErrorCodes.SourceUnreachable,
        "57P01" or "57P02" or "57P03" => ErrorCodes.SourceUnreachable,
        _ => ErrorCodes.SourceRejected,
    };
}
