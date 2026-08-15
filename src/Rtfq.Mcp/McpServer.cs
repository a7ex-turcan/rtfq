using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfq.Client;

namespace Rtfq.Mcp;

/// <summary>
/// The MCP surface: JSON-RPC 2.0 over stdio, mapping tool calls onto the HTTP
/// client. Thin by design — no policy, no caching, no cleverness. Everything that
/// could be called a rule lives on the server, where talking to the port directly
/// cannot bypass it.
///
/// The protocol is hand-rolled rather than taken from an SDK. Two reasons, both
/// in CLAUDE.md: MCP is still moving, and coupling to a churning dependency to
/// save two hundred lines of well-specified JSON-RPC is a poor trade — and the
/// binary must stay AOT-clean, which rules out reflection-driven tool discovery.
///
/// <b>stdout is the protocol channel.</b> Nothing may write to it but responses;
/// diagnostics go to stderr.
/// </summary>
public sealed class McpServer(RtfqClient client, TextReader input, TextWriter output)
{
    const string DefaultProtocolVersion = "2025-06-18";
    const string ServerName = "rtfq";

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null) return;                       // stdin closed: the host is done with us
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonObject request;
            try
            {
                request = JsonNode.Parse(line)?.AsObject()
                          ?? throw new JsonException("not a JSON object");
            }
            catch (JsonException ex)
            {
                await WriteAsync(Error(null, -32700, "Parse error: " + ex.Message)).ConfigureAwait(false);
                continue;
            }

            var response = await HandleAsync(request, cancellationToken).ConfigureAwait(false);

            // Notifications have no id and take no response.
            if (response is not null) await WriteAsync(response).ConfigureAwait(false);
        }
    }

    async Task<JsonObject?> HandleAsync(JsonObject request, CancellationToken cancellationToken)
    {
        var id = request["id"]?.DeepClone();
        var method = request["method"]?.GetValue<string>();

        switch (method)
        {
            case "initialize":
                // Echo the client's protocol version when it names one: this server
                // exposes tools only, and that surface has been stable across
                // revisions, so refusing on a version mismatch would fail for no
                // reason a user could act on.
                var requested = request["params"]?["protocolVersion"]?.GetValue<string>() ?? DefaultProtocolVersion;
                return Result(id, new JsonObject
                {
                    ["protocolVersion"] = requested,
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject
                    {
                        ["name"] = ServerName,
                        ["version"] = Contracts.RtfqVersion.Current,
                    },
                });

            case "notifications/initialized" or "notifications/cancelled":
                return null;

            case "ping":
                return Result(id, new JsonObject());

            case "tools/list":
                return Result(id, new JsonObject { ["tools"] = ToolCatalog.Describe() });

            case "tools/call":
                return await CallToolAsync(id, request["params"]?.AsObject(), cancellationToken).ConfigureAwait(false);

            case null:
                return Error(id, -32600, "Invalid request: no method");

            default:
                // Notifications are fire-and-forget; unknown requests are an error.
                return id is null ? null : Error(id, -32601, $"Method not found: {method}");
        }
    }

    async Task<JsonObject> CallToolAsync(JsonNode? id, JsonObject? parameters, CancellationToken cancellationToken)
    {
        var name = parameters?["name"]?.GetValue<string>();
        if (string.IsNullOrEmpty(name)) return Error(id, -32602, "tools/call requires a name");

        var arguments = parameters?["arguments"]?.AsObject() ?? [];

        try
        {
            var text = await ToolCatalog.InvokeAsync(client, name, arguments, cancellationToken).ConfigureAwait(false);
            return Result(id, Content(text, isError: false));
        }
        catch (RtfqClientException ex)
        {
            // A refusal is a tool result, not a protocol error: the agent needs to
            // read the code and adapt, and a JSON-RPC error would deny it that.
            return Result(id, Content($"[{ex.Code}] {ex.Message}", isError: true));
        }
        catch (ArgumentException ex)
        {
            return Result(id, Content($"[request.malformed] {ex.Message}", isError: true));
        }
        catch (HttpRequestException ex)
        {
            return Result(id, Content($"[source.unreachable] cannot reach the rtfq server: {ex.Message}", isError: true));
        }
    }

    static JsonObject Content(string text, bool isError) => new()
    {
        ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        ["isError"] = isError,
    };

    static JsonObject Result(JsonNode? id, JsonObject result) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["result"] = result,
    };

    static JsonObject Error(JsonNode? id, int code, string message) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id,
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    };

    async Task WriteAsync(JsonObject message)
    {
        await output.WriteLineAsync(message.ToJsonString()).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
