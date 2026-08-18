using System.Collections.Concurrent;
using Rtfq.Contracts;

namespace Rtfq.Server.Policy;

/// <param name="Level">The highest access the unlock opens. Read is never locked.</param>
public sealed record UnlockState(string Source, AccessLevel Level, string Who, DateTimeOffset ExpiresAt);

/// <summary>
/// Time-boxed unlock: writing stays off at runtime even where the config permits
/// it, until somebody deliberately opens it.
///
/// The point is that a config granting write is a statement about what is
/// *possible*, not about what should be possible right now. Copying a staging
/// config to production, or leaving a write grant in place after the incident it
/// was added for, should not leave a loaded gun lying around.
///
/// Deliberately in memory, and deliberately not persisted: <b>a restart
/// re-locks</b>. That is not configurable, because an unlock that survives the
/// thing it was scoped to is not time-boxed.
/// </summary>
public sealed class UnlockStore
{
    readonly ConcurrentDictionary<string, UnlockState> _unlocked = new(StringComparer.Ordinal);

    /// <summary>Longest an unlock may last. A window measured in hours is not a window.</summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(1);

    public UnlockState Unlock(string source, AccessLevel level, TimeSpan ttl, string who)
    {
        if (ttl > MaxTtl) ttl = MaxTtl;
        if (ttl <= TimeSpan.Zero) ttl = TimeSpan.FromMinutes(15);

        var state = new UnlockState(source, level, who, DateTimeOffset.UtcNow + ttl);
        _unlocked[source] = state;
        return state;
    }

    public void Lock(string source) => _unlocked.TryRemove(source, out _);

    /// <summary>
    /// Whether <paramref name="source"/> is currently open to
    /// <paramref name="required"/>. Expiry is evaluated on read rather than swept,
    /// so a lapsed unlock is closed the instant it lapses instead of whenever a
    /// timer next fires.
    /// </summary>
    public bool IsUnlocked(string source, AccessLevel required)
    {
        if (required <= AccessLevel.Read) return true;
        if (!_unlocked.TryGetValue(source, out var state)) return false;

        if (state.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _unlocked.TryRemove(source, out _);
            return false;
        }

        return state.Level >= required;
    }

    public IReadOnlyList<UnlockState> Current()
    {
        var now = DateTimeOffset.UtcNow;
        return [.. _unlocked.Values.Where(s => s.ExpiresAt > now).OrderBy(s => s.Source, StringComparer.Ordinal)];
    }
}
