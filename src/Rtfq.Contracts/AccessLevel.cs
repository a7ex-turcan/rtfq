namespace Rtfq.Contracts;

/// <summary>
/// The three nesting permission levels. Ordering is load-bearing: the effective
/// permission for a call is the <b>minimum</b> of the source's declared access and
/// the caller's grant, which is why these are ordered rather than flags.
/// Default is <see cref="Read"/> — absent means read, never write.
/// </summary>
public enum AccessLevel
{
    Read = 0,
    Write = 1,

    /// <summary>
    /// Additive and corrective DDL, per ADR 0002. Implies <see cref="Write"/>.
    /// A source may only declare this if its adapter can do transactional DDL.
    /// </summary>
    Schema = 2,
}

public static class AccessLevels
{
    public static string ToWire(this AccessLevel level) => level switch
    {
        AccessLevel.Read => "read",
        AccessLevel.Write => "write",
        AccessLevel.Schema => "schema",
        _ => "read",
    };

    /// <summary>Parses a config value. Returns false rather than throwing so the caller can report a line number.</summary>
    public static bool TryParse(string? text, out AccessLevel level)
    {
        switch (text?.Trim().ToLowerInvariant())
        {
            case null or "": level = AccessLevel.Read; return true;   // absent means read
            case "read": level = AccessLevel.Read; return true;
            case "write": level = AccessLevel.Write; return true;
            case "schema": level = AccessLevel.Schema; return true;
            default: level = AccessLevel.Read; return false;
        }
    }

    /// <summary>The effective permission: the lower of what the source allows and what the caller was granted.</summary>
    public static AccessLevel Intersect(AccessLevel sourceAllows, AccessLevel callerGranted) =>
        sourceAllows < callerGranted ? sourceAllows : callerGranted;
}
