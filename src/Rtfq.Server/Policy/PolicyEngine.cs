using Rtfq.Contracts;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Policy;

/// <param name="Grants">What this caller was granted, per source. Absent means no grant, which means no access.</param>
public sealed record Caller(string TokenId, IReadOnlyDictionary<string, AccessLevel> Grants);

public enum Outcome
{
    Allow,

    /// <summary>
    /// The source does not exist, or the caller holds no grant on it. One outcome
    /// for both on purpose: distinguishing them tells an unauthorised caller which
    /// sources exist.
    /// </summary>
    SourceUnknown,

    /// <summary>The caller may reach the source, but not at the level this operation needs.</summary>
    InsufficientAccess,
}

public sealed record PolicyDecision(Outcome Outcome, AccessLevel Effective, string? ErrorCode, string Message)
{
    public bool Allowed => Outcome == Outcome.Allow;
}

/// <summary>
/// The single place permission is decided. Default-deny, and the effective
/// permission is the <b>intersection</b> of what the source declares and what the
/// caller was granted — so enabling writes always takes two edits in two places,
/// and copying a staging config to production is not sufficient to hand an agent
/// a loaded gun.
///
/// M3 adds the target allow-list and the mutation guard as further gates. They
/// belong here or below, never in a handler.
/// </summary>
public sealed class PolicyEngine(RtfqConfig config)
{
    public PolicyDecision Evaluate(Caller caller, string sourceName, AccessLevel required)
    {
        var source = config.FindSource(sourceName);
        if (source is null)
        {
            return new(Outcome.SourceUnknown, AccessLevel.Read, ErrorCodes.SourceUnknown,
                $"no source '{sourceName}' is available to this token");
        }

        if (!caller.Grants.TryGetValue(sourceName, out var granted))
        {
            return new(Outcome.SourceUnknown, AccessLevel.Read, ErrorCodes.SourceUnknown,
                $"no source '{sourceName}' is available to this token");
        }

        var effective = AccessLevels.Intersect(source.Access, granted);

        if (effective < required)
        {
            return new(Outcome.InsufficientAccess, effective, ErrorCodes.InsufficientAccess,
                $"'{sourceName}' resolves to {effective.ToWire()} for this token, which does not permit {required.ToWire()}");
        }

        return new(Outcome.Allow, effective, null, "allowed");
    }

    /// <summary>Effective access per source for this caller, for <c>list_sources</c>. Sources with no grant are omitted entirely.</summary>
    public IEnumerable<(SourceSection Source, AccessLevel Effective)> VisibleSources(Caller caller)
    {
        foreach (var source in config.Sources)
        {
            if (!caller.Grants.TryGetValue(source.Name, out var granted)) continue;
            yield return (source, AccessLevels.Intersect(source.Access, granted));
        }
    }
}
