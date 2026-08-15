using MongoDB.Bson;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Mongo;

/// <param name="Command">The command document, bounded where it was not already.</param>
/// <param name="Collection">The collection the command targets, for auditing and allow-lists.</param>
public readonly record struct GuardedCommand(BsonDocument Command, string Collection, bool Rewritten);

/// <summary>
/// The read guard for MongoDB.
///
/// MongoDB's "statement" is not a string of SQL — it is a command document, and
/// that is its native dialect, so a caller sends JSON like
/// <c>{"find": "orders", "filter": {"status": "stuck"}}</c>. The adapter parses
/// its own dialect; nothing above this layer learns that Mongo is different.
///
/// The shape is the same as ADR 0001 established for SQL: an allow-list of
/// commands, an exhaustive walk, and refusal by default. Mongo has its own
/// version of "not DDL, but catastrophic" — <c>$out</c> and <c>$merge</c> are
/// aggregation <i>stages</i> that write a collection, and <c>$where</c>,
/// <c>$function</c> and <c>$accumulator</c> execute server-side JavaScript. None
/// of those is a write command; all of them would be catastrophic through a
/// read-only tool, and a command-name allow-list alone misses every one.
/// </summary>
public static class MongoReadGuard
{
    static readonly HashSet<string> ReadCommands = new(StringComparer.Ordinal)
    {
        "find", "aggregate", "count", "distinct", "listCollections", "listIndexes",
    };

    /// <summary>
    /// Refused wherever they appear, at any depth. These are operators rather than
    /// commands, so the command name says nothing about their presence.
    /// </summary>
    static readonly HashSet<string> ForbiddenOperators = new(StringComparer.Ordinal)
    {
        "$out",          // writes the pipeline result into a collection
        "$merge",        // upserts the pipeline result into a collection
        "$where",        // server-side JavaScript predicate
        "$function",     // server-side JavaScript expression
        "$accumulator",  // server-side JavaScript accumulator
        "$eval",         // deprecated, still arbitrary execution
    };

    public static GuardedCommand Prepare(string statement, int? maxRows)
    {
        BsonDocument command;
        try
        {
            command = BsonDocument.Parse(statement);
        }
        catch (Exception ex) when (ex is FormatException or IOException)
        {
            throw new AdapterException(ErrorCodes.SourceRejected,
                $"not a MongoDB command document: {ex.Message}. Expected something like " +
                "{\"find\": \"orders\", \"filter\": {...}}", ex);
        }

        if (command.ElementCount == 0)
            throw new AdapterException(ErrorCodes.StatementEmpty, "empty command document");

        // The command name is the FIRST field, per the wire protocol. Taking any
        // other field would let a caller bury the real command behind a decoy.
        var name = command.GetElement(0).Name;

        if (!ReadCommands.Contains(name))
        {
            var reason = name is "insert" or "update" or "delete" or "findAndModify" or "findandmodify"
                ? "this token has read access; writes arrive in M3"
                : $"'{name}' is not a read command";
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {reason}");
        }

        foreach (var forbidden in FindForbidden(command))
        {
            var why = forbidden is "$out" or "$merge"
                ? $"{forbidden} writes its result into a collection"
                : $"{forbidden} executes server-side JavaScript";
            throw new AdapterException(ErrorCodes.InsufficientAccess, $"refused: {why}");
        }

        var collection = command.GetElement(0).Value is BsonString target
            ? target.AsString
            : name;

        if (maxRows is null) return new GuardedCommand(command, collection, false);
        return Bound(command, name, collection, maxRows.Value);
    }

    /// <summary>
    /// Bounds the result. <c>find</c> takes a limit field; <c>aggregate</c> takes a
    /// <c>$limit</c> stage appended to the pipeline, which is the only way to cap a
    /// pipeline whose stages may each expand the document count.
    /// </summary>
    static GuardedCommand Bound(BsonDocument command, string name, string collection, int maxRows)
    {
        switch (name)
        {
            case "find":
            {
                if (command.TryGetValue("limit", out var existing)
                    && existing.IsNumeric
                    && existing.ToInt32() > 0
                    && existing.ToInt32() <= maxRows)
                {
                    return new GuardedCommand(command, collection, false);
                }
                command["limit"] = maxRows;
                return new GuardedCommand(command, collection, true);
            }

            case "aggregate":
            {
                if (command["pipeline"] is not BsonArray pipeline)
                    throw new AdapterException(ErrorCodes.SourceRejected, "aggregate requires a pipeline array");

                pipeline.Add(new BsonDocument("$limit", maxRows));

                // A cursor document is required by the aggregate command; supply
                // one rather than making the caller remember protocol trivia.
                if (!command.Contains("cursor")) command["cursor"] = new BsonDocument();

                return new GuardedCommand(command, collection, true);
            }

            default:
                // count and distinct return a scalar, so there is nothing to bound.
                return new GuardedCommand(command, collection, false);
        }
    }

    /// <summary>
    /// Walks the whole document for forbidden operator names. Exhaustive rather
    /// than top-level: <c>$out</c> is legal only as the last pipeline stage, but a
    /// guard that only checks there would miss it inside a <c>$facet</c>.
    /// </summary>
    static IEnumerable<string> FindForbidden(BsonValue value)
    {
        switch (value)
        {
            case BsonDocument document:
                foreach (var element in document)
                {
                    if (ForbiddenOperators.Contains(element.Name)) yield return element.Name;
                    foreach (var nested in FindForbidden(element.Value)) yield return nested;
                }
                break;

            case BsonArray array:
                foreach (var item in array)
                    foreach (var nested in FindForbidden(item)) yield return nested;
                break;
        }
    }
}
