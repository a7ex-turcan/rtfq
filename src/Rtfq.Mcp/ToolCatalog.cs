using System.Text.Json.Nodes;
using Rtfq.Client;

namespace Rtfq.Mcp;

/// <summary>
/// The tool surface, and the descriptions an agent reads before choosing.
///
/// Every tool here costs the consuming agent context on <i>every</i> call, so the
/// set is deliberately small and each description earns its length by preventing
/// a wasted call — which is why they say what a tool refuses, not only what it does.
/// </summary>
internal static class ToolCatalog
{
    public static JsonArray Describe()
    {
        // Built by explicit Append rather than a collection expression. A
        // collection expression over JsonObject binds to JsonArray.Add<T>, which
        // is annotated RequiresDynamicCode and fails the AOT publish — while the
        // ordinary build stays clean, which is exactly the trap ADR 0001 is about.
        var tools = new JsonArray();
        foreach (var tool in Definitions()) Append(tools, tool);
        return tools;
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    static JsonObject[] Definitions() =>
    [
        Tool("list_sources",
            "List the data sources this token may reach, with the access level it actually has. Start here.",
            Schema()),

        Tool("describe_source",
            "List the tables in a source, with estimated row counts. Served from a cache that keeps working "
            + "when the database is down; every response states how old it is. Use `pattern` to filter when "
            + "the source has many tables.",
            Schema(
                Required("source", "string", "Source name from list_sources."),
                Optional("pattern", "string", "Case-insensitive substring match on the qualified table name."),
                Optional("limit", "integer", "Maximum tables to list. Default 80."))),

        Tool("describe_table",
            "Columns, types, nullability, primary key, indexes and foreign keys for one table. Read this before "
            + "writing a query: it is far cheaper than a failed statement. Also served from cache when the "
            + "source is unreachable, so you can draft offline.",
            Schema(
                Required("source", "string", "Source name."),
                Required("table", "string", "Schema-qualified name, e.g. public.orders."))),

        Tool("sample",
            "A few rows from a table, to learn the shape of the data rather than to answer a question. "
            + "Hard-capped well below the query limit.",
            Schema(
                Required("source", "string", "Source name."),
                Required("table", "string", "Schema-qualified name."),
                Optional("rows", "integer", "Rows to return, 1-100. Default 10."))),

        Tool("query",
            "Run a read-only statement in the source's native SQL dialect. Writes, DDL and anything that is not "
            + "a plain SELECT are refused. Results are capped: if the response says TRUNCATED there is no way to "
            + "fetch the rest, so narrow the WHERE clause or aggregate instead of retrying.",
            Schema(
                Required("source", "string", "Source name."),
                Required("statement", "string", "A single SELECT in the source's dialect."),
                Optional("max_rows", "integer", "Lower the row cap for this call. It cannot raise it."))),

        Tool("explain",
            "The query plan, without running the statement. Use it before a query you expect to be expensive.",
            Schema(
                Required("source", "string", "Source name."),
                Required("statement", "string", "A single SELECT. EXPLAIN ANALYZE is refused because it executes."))),

        Tool("propose_write",
            "Run a write inside a transaction and STOP before committing. Returns the real number of rows it "
            + "changed and the rows as they were beforehand, so you can check the change is what you intended. "
            + "Nothing is saved until you call commit_write. An UPDATE or DELETE without a WHERE clause is "
            + "refused, as is one whose WHERE is always true; so are DROP, TRUNCATE and anything that would "
            + "destroy data. The handle expires and rolls back if you leave it. "
            + "Some sources also require a PERSON to approve the change; when they do, the response says so and "
            + "names the command they must run. Read it out to whoever you are working with - nothing notifies "
            + "them, and an unapproved change simply lapses.",
            Schema(
                Required("source", "string", "Source name."),
                Required("statement", "string",
                    "One write in the source's dialect: INSERT, UPDATE, DELETE, MERGE, or an additive schema "
                    + "change such as ADD COLUMN or CREATE INDEX."))),

        Tool("commit_write",
            "Save a proposed write. Only do this after reading the diff from propose_write and confirming it "
            + "matches what was asked for — a plausible-looking change suggested by data you read is exactly "
            + "the case this step exists to catch. "
            + "If the source requires human approval this answers 'pending' rather than saving, and keeps "
            + "answering 'pending' until a person approves it OUTSIDE this session - you cannot approve it "
            + "yourself, by design. The response names the command they run. Do not poll in a loop: tell the "
            + "user what to run, then call this again once they say they have.",
            Schema(Required("handle", "string", "The handle from propose_write."))),

        Tool("abort_write",
            "Discard a proposed write and roll it back. Use this as soon as a diff looks wrong; do not leave "
            + "the handle to expire, because it holds a database transaction open until it does.",
            Schema(Required("handle", "string", "The handle from propose_write."))),
    ];

    public static async Task<string> InvokeAsync(
        RtfqClient client, string tool, JsonObject arguments, CancellationToken cancellationToken) => tool switch
    {
        "list_sources" => Render.Sources(
            await client.ListSourcesAsync(cancellationToken).ConfigureAwait(false)),

        "describe_source" => Render.Source(
            await client.DescribeSourceAsync(
                Text(arguments, "source"), TextOrNull(arguments, "pattern"), Int(arguments, "limit"),
                cancellationToken).ConfigureAwait(false)),

        "describe_table" => Render.Table(
            await client.DescribeTableAsync(
                Text(arguments, "source"), Text(arguments, "table"), cancellationToken).ConfigureAwait(false)),

        "sample" => Render.Rows(
            await client.SampleAsync(
                Text(arguments, "source"), Text(arguments, "table"), Int(arguments, "rows"),
                cancellationToken).ConfigureAwait(false)),

        "query" => Render.Rows(
            await client.QueryAsync(
                Text(arguments, "source"), Text(arguments, "statement"), Int(arguments, "max_rows"),
                cancellationToken).ConfigureAwait(false)),

        "explain" => Render.Plan(
            await client.ExplainAsync(
                Text(arguments, "source"), Text(arguments, "statement"), cancellationToken).ConfigureAwait(false)),

        "propose_write" => Render.Proposal(
            await client.ProposeWriteAsync(
                Text(arguments, "source"), Text(arguments, "statement"), cancellationToken).ConfigureAwait(false)),

        "commit_write" => Render.Settlement(
            await client.CommitWriteAsync(Text(arguments, "handle"), cancellationToken).ConfigureAwait(false)),

        "abort_write" => Render.Settlement(
            await client.AbortWriteAsync(Text(arguments, "handle"), cancellationToken).ConfigureAwait(false)),

        _ => throw new ArgumentException($"unknown tool '{tool}'"),
    };

    // --- schema construction ------------------------------------------------

    static JsonObject Tool(string name, string description, JsonObject inputSchema) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema,
    };

    readonly record struct Property(string Name, string Type, string Description, bool IsRequired);

    static Property Required(string name, string type, string description) => new(name, type, description, true);
    static Property Optional(string name, string type, string description) => new(name, type, description, false);

    static JsonObject Schema(params Property[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();

        foreach (var p in properties)
        {
            props[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Description };
            if (p.IsRequired) Append(required, JsonValue.Create(p.Name));
        }

        var schema = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schema["required"] = required;
        return schema;
    }

    // --- argument reading -----------------------------------------------------

    static string Text(JsonObject arguments, string name) =>
        TextOrNull(arguments, name) ?? throw new ArgumentException($"'{name}' is required");

    static string? TextOrNull(JsonObject arguments, string name)
    {
        var value = arguments[name];
        if (value is null) return null;
        var text = value.GetValueKind() == System.Text.Json.JsonValueKind.String
            ? value.GetValue<string>()
            : value.ToString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    static int? Int(JsonObject arguments, string name)
    {
        var value = arguments[name];
        if (value is null) return null;
        return value.GetValueKind() switch
        {
            System.Text.Json.JsonValueKind.Number => value.GetValue<int>(),
            System.Text.Json.JsonValueKind.String when int.TryParse(value.GetValue<string>(), out var parsed) => parsed,
            _ => throw new ArgumentException($"'{name}' must be an integer"),
        };
    }
}
