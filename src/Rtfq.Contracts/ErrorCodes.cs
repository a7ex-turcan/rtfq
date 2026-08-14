namespace Rtfq.Contracts;

/// <summary>
/// Stable, machine-readable refusal reasons in <c>domain.reason</c> form.
///
/// Agents branch on these, so they are API surface from the first release:
/// changing or removing one is a breaking change, exactly like changing a
/// response field. Add new codes rather than repurposing existing ones.
///
/// The distinction that matters to a caller is "can I fix this by changing my
/// request?" (<c>request.*</c>, <c>policy.*</c>) versus "is this the server's or
/// the source's problem?" (<c>source.*</c>, <c>internal.*</c>).
/// </summary>
public static class ErrorCodes
{
    // --- authentication: the caller is not who they say they are -----------
    public const string TokenMissing = "auth.token_missing";
    public const string TokenInvalid = "auth.token_invalid";

    // --- policy: the caller is known but not permitted ---------------------
    /// <summary>
    /// The source is not declared in config, OR the caller holds no grant on it.
    /// Deliberately the same code for both: telling an unauthorised caller which
    /// sources exist is an information leak, and the fix is identical either way.
    /// </summary>
    public const string SourceUnknown = "policy.source_unknown";

    /// <summary>The caller may reach the source, but not at the level this operation needs.</summary>
    public const string InsufficientAccess = "policy.insufficient_access";

    // --- request: the caller can fix this -----------------------------------
    public const string RequestMalformed = "request.malformed";
    public const string StatementEmpty = "request.statement_empty";

    // --- source: the data source's problem, not ours -------------------------
    public const string SourceUnreachable = "source.unreachable";
    public const string SourceTimeout = "source.timeout";
    /// <summary>The engine parsed and rejected the statement. The message is the engine's.</summary>
    public const string SourceRejected = "source.rejected";

    // --- config: startup and validation --------------------------------------
    public const string ConfigInvalid = "config.invalid";

    // --- ours -----------------------------------------------------------------
    public const string Internal = "internal.error";
}
