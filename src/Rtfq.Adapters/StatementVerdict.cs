namespace Rtfq.Adapters;

/// <summary>What a statement is, once a real parser has looked at it.</summary>
public enum StatementKind
{
    Read,

    /// <summary>DML: bounded, qualified, and subject to the affected-row cap.</summary>
    Mutation,

    /// <summary>Additive or corrective schema change (ADR 0002). The row cap does not apply.</summary>
    Schema,
}

/// <summary>
/// A statement cleared to run, and everything the gates above need to decide
/// about it.
///
/// The guard returns facts rather than permission: it says what the statement is
/// and what it touches, and the policy engine decides whether this caller may do
/// that. Keeping those separate is what stops dialect knowledge leaking into
/// policy, and policy leaking into adapters.
/// </summary>
public sealed record GuardedStatement
{
    public required StatementKind Kind { get; init; }

    /// <summary>The statement to execute, bounded where the guard bounded it.</summary>
    public required string Statement { get; init; }

    public bool Rewritten { get; init; }

    /// <summary>
    /// The object a mutation or schema change writes to, schema-qualified. Empty
    /// for a read.
    /// </summary>
    public string Target { get; init; } = "";

    /// <summary>
    /// Every object the statement reads or writes, schema-qualified. Deny rules
    /// apply to all of them — a SELECT that joins a denied table is still reading
    /// it, however the FROM clause is arranged.
    /// </summary>
    public IReadOnlyList<string> Referenced { get; init; } = [];

    /// <summary>
    /// A SELECT over exactly the rows this statement is about to change, for
    /// capturing before-images. Built from the parse tree rather than by string
    /// surgery, and null for INSERT and for schema changes, which have no prior
    /// rows to journal.
    /// </summary>
    public string? BeforeImageQuery { get; init; }

    /// <summary>
    /// A one-line description of the schema change, for the audit journal — the
    /// engine's own catalog holds the real prior definition.
    /// </summary>
    public string? SchemaSummary { get; init; }
}
