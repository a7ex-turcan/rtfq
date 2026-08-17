using Rtfq.Contracts;

namespace Rtfq.Adapters.Postgres;

/// <summary>
/// The read-only face of <see cref="PostgresWriteGuard"/>.
///
/// One guard, two entry points. Before M3 this was the whole implementation; now
/// the full guard classifies read / mutation / schema, and this narrows that to
/// "must be a read" for the callers — <c>query</c>, <c>sample</c>, <c>explain</c>
/// — that may never do anything else.
///
/// Kept as a distinct entry point rather than a parameter because a caller that
/// wants a read should have to say so, and a caller that forgets should get a
/// read-only guard rather than a permissive one.
/// </summary>
public static class PostgresReadGuard
{
    /// <summary>
    /// Validates a read and, when <paramref name="maxRows"/> is given, injects a
    /// row limit. Pass null to validate without rewriting — which is what
    /// <c>explain</c> needs, since a limit the caller did not write would change
    /// the plan they asked to see.
    /// </summary>
    /// <exception cref="AdapterException">The statement is not a plain read.</exception>
    public static GuardedRead Prepare(string sql, int? maxRows)
    {
        var verdict = PostgresWriteGuard.Prepare(sql, maxRows);

        if (verdict.Kind != StatementKind.Read)
        {
            var what = verdict.Kind == StatementKind.Mutation
                ? "this token has read access; writes go through propose_write"
                : "this token has read access; schema changes go through propose_write";

            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {what}");
        }

        return new GuardedRead(verdict.Statement, verdict.Rewritten);
    }
}
