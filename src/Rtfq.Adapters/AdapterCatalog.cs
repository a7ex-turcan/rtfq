namespace Rtfq.Adapters;

/// <summary>
/// What source kinds exist, and what each can do <i>without being connected to</i>.
///
/// This type exists because the M2 interface audit found the knowledge duplicated:
/// config validation had its own hardcoded lists of which kinds support
/// transactional writes and DDL, which is capability knowledge living above the
/// adapter layer. Adding an engine would then have meant editing the validator —
/// exactly the leak the audit is meant to catch.
///
/// Everything here is the <b>declared</b> baseline, knowable from the kind alone.
/// Anything that depends on the deployment — MongoDB's transactions need a
/// replica set — is deliberately absent, because it can only be answered by
/// <see cref="ISourceAdapter.CheckAsync"/> against a live server.
/// </summary>
public static class AdapterCatalog
{
    static readonly Dictionary<string, SourceCapabilities> Declared = new(StringComparer.Ordinal)
    {
        ["postgres"] = new(TransactionalWrites: true, TransactionalDdl: true, Explain: true, Introspection: true),
        ["mssql"] = new(TransactionalWrites: true, TransactionalDdl: true, Explain: true, Introspection: true),

        // Optimistic on writes: a replica set can do them, and refusing every
        // Mongo source offline would reject a valid config. The startup check
        // settles it against the actual topology.
        ["mongodb"] = new(TransactionalWrites: true, TransactionalDdl: false, Explain: true, Introspection: true),

        // An HTTP API has no transaction to roll back under any circumstances,
        // so this one IS decidable offline.
        ["http"] = new(TransactionalWrites: false, TransactionalDdl: false, Explain: false, Introspection: true),
    };

    public static IReadOnlyCollection<string> Kinds => Declared.Keys;

    public static bool IsKnown(string kind) => Declared.ContainsKey(kind);

    /// <summary>
    /// The best that can be said about a kind before connecting. Unknown kinds
    /// report nothing rather than throwing, so a validator can report the unknown
    /// kind itself as the error instead of failing on a lookup.
    /// </summary>
    public static SourceCapabilities? DeclaredCapabilities(string kind) =>
        Declared.TryGetValue(kind, out var capabilities) ? capabilities : null;
}
