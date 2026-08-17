using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Rtfq.Contracts;

namespace Rtfq.Adapters.Http;

/// <summary>
/// An HTTP API as a read source.
///
/// The native dialect here is a request line: <c>GET /v1/invoices?status=open</c>.
/// That keeps the adapter interface unchanged — a statement is still a string in
/// the source's own language, exactly as SQL and a Mongo command document are.
///
/// There is no query engine to guard, so the gate is the allow-list: a method
/// that is not permitted and a path that is not matched are both refused before
/// anything leaves the process. An HTTP source with no <c>allow_paths</c> reaches
/// nothing, because an empty allow-list is empty rather than open.
/// </summary>
public sealed class HttpAdapter : ISourceAdapter
{
    readonly HttpClient _http;
    readonly string[] _methods;
    readonly string[] _allowPaths;

    public string Name { get; }
    public string Kind => "http";

    public SourceCapabilities Capabilities { get; } = new(
        // No transactions exist to have. A source that cannot roll back must never
        // be marked writable, which config validation enforces.
        TransactionalWrites: false,
        TransactionalDdl: false,
        Explain: false,
        Introspection: true);

    public HttpAdapter(
        string name,
        string baseUrl,
        IReadOnlyList<string> methods,
        IReadOnlyList<string> allowPaths,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout)
    {
        Name = name;

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            throw new AdapterException(ErrorCodes.ConfigInvalid, $"source '{name}' has an invalid base_url '{baseUrl}'");

        // Absent means GET, never "all". An unstated method list is the common way
        // a config accidentally permits more than its author meant.
        _methods = methods.Count > 0
            ? [.. methods.Select(m => m.ToUpperInvariant())]
            : ["GET"];

        _allowPaths = [.. allowPaths];

        _http = new HttpClient { BaseAddress = uri, Timeout = timeout };
        foreach (var (key, value) in headers)
        {
            if (!_http.DefaultRequestHeaders.TryAddWithoutValidation(key, value))
                throw new AdapterException(ErrorCodes.ConfigInvalid, $"source '{name}' has an invalid header '{key}'");
        }
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<SourceCapabilities> CheckAsync(CancellationToken cancellationToken)
    {
        try
        {
            // No health convention exists across APIs, so reachability is the most
            // that can honestly be checked: the base URL answers something.
            using var request = new HttpRequestMessage(HttpMethod.Options, "");
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return Capabilities;
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    /// <summary>
    /// "Introspection" for an HTTP source is the allow-list itself. There is
    /// nothing to interrogate, and inventing a discovery protocol would be
    /// pretending to know more than the config says.
    /// </summary>
    public Task<SchemaSnapshot> IntrospectAsync(CancellationToken cancellationToken)
    {
        var endpoints = _allowPaths.Select(path => new TableSchema
        {
            Schema = "endpoints",
            Name = path,
            Kind = "endpoint",
            EstimatedRows = null,
            Columns = [.. _methods.Select(m => new ColumnSchema { Name = m, Type = "method", Nullable = false })],
        }).ToList();

        return Task.FromResult(new SchemaSnapshot
        {
            Source = Name,
            CapturedAt = DateTimeOffset.UtcNow,
            Tables = endpoints,
        });
    }

    public Task<ReadResult> SampleAsync(string table, int rows, CancellationToken cancellationToken) =>
        ExecuteReadAsync($"GET {table}", new ReadOptions(rows, _http.Timeout), cancellationToken);

    public async Task<ReadResult> ExecuteReadAsync(string statement, ReadOptions options, CancellationToken cancellationToken)
    {
        var (method, path) = ParseRequestLine(statement);

        if (!_methods.Contains(method, StringComparer.Ordinal))
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {method} is not permitted on '{Name}' (allowed: {string.Join(", ", _methods)})");

        if (!IsPathAllowed(path))
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: '{path}' is not on the allow-list for '{Name}'");

        // Reads only until M3, whatever the config permits: a method being
        // configured does not make it a read.
        if (method != "GET" && method != "HEAD")
            throw new AdapterException(ErrorCodes.InsufficientAccess,
                $"refused: {method} is a write; writes arrive in M3");

        try
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path.TrimStart('/'));
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                throw new AdapterException(ErrorCodes.SourceRejected,
                    $"{(int)response.StatusCode} {response.ReasonPhrase}: {Truncate(body, 300)}");
            }

            return Tabulate(body, options.MaxRows);
        }
        catch (Exception ex)
        {
            throw Translate(ex);
        }
    }

    public Task<string> ExplainAsync(string statement, TimeSpan timeout, CancellationToken cancellationToken) =>
        throw new AdapterException(ErrorCodes.SourceRejected, "an HTTP source has no query plan to explain");

    public GuardedStatement Classify(string statement)
    {
        var (method, path) = ParseRequestLine(statement);
        return new GuardedStatement
        {
            Kind = method is "GET" or "HEAD" ? StatementKind.Read : StatementKind.Mutation,
            Statement = statement,
            Target = path.Split('?')[0],
            Referenced = [path.Split('?')[0]],
        };
    }

    /// <summary>
    /// Never. An HTTP API has no transaction to leave open, so there is no way to
    /// show a caller what a change did before deciding whether to keep it — and
    /// the propose/commit split is the whole safety mechanism, not a formality.
    /// Config validation refuses <c>access: write</c> on an HTTP source for the
    /// same reason; this is the backstop.
    /// </summary>
    public Task<IMutationTransaction> BeginMutationAsync(
        GuardedStatement statement, MutationOptions options, CancellationToken cancellationToken) =>
        throw new AdapterException(ErrorCodes.InsufficientAccess,
            "refused: an HTTP source has no transactions, so a change could not be rolled back after you saw it");

    static (string Method, string Path) ParseRequestLine(string statement)
    {
        var trimmed = statement.Trim();
        var space = trimmed.IndexOf(' ', StringComparison.Ordinal);

        if (space <= 0)
            throw new AdapterException(ErrorCodes.SourceRejected,
                $"expected a request line like 'GET /v1/invoices', got '{Truncate(trimmed, 80)}'");

        return (trimmed[..space].ToUpperInvariant(), trimmed[(space + 1)..].Trim());
    }

    /// <summary>
    /// Prefix wildcards only, and only at the end. Anything richer invites a
    /// pattern that reads as narrow and matches broadly.
    /// </summary>
    bool IsPathAllowed(string path)
    {
        var withoutQuery = path.Split('?')[0];

        foreach (var allowed in _allowPaths)
        {
            if (allowed.EndsWith('*'))
            {
                if (withoutQuery.StartsWith(allowed[..^1], StringComparison.Ordinal)) return true;
            }
            else if (string.Equals(withoutQuery, allowed, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Flattens a JSON response into the shared columnar envelope: an array of
    /// objects becomes rows, a single object becomes one row, and anything else
    /// becomes a single-cell result rather than an error.
    /// </summary>
    static ReadResult Tabulate(string body, int maxRows)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(body);
        }
        catch (JsonException)
        {
            return Single("body", body);
        }

        var items = parsed switch
        {
            JsonArray array => array.ToList(),
            JsonObject obj when obj.Count == 1 && obj.First().Value is JsonArray inner => inner.ToList(),
            JsonObject obj => [obj],
            _ => null,
        };

        if (items is null) return Single("value", parsed?.ToString() ?? "null");

        var order = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in items.Take(maxRows).OfType<JsonObject>())
        {
            foreach (var (key, _) in item)
                if (seen.Add(key)) order.Add(key);
        }

        if (order.Count == 0) return Single("value", parsed?.ToJsonString() ?? "null");

        var rows = new JsonArray();
        var truncated = false;

        foreach (var item in items)
        {
            if (rows.Count >= maxRows) { truncated = true; break; }

            var row = new JsonArray();
            foreach (var field in order)
            {
                var cell = item is JsonObject obj && obj.TryGetPropertyValue(field, out var value) ? value : null;
                Append(row, cell?.DeepClone());
            }
            Append(rows, row);
        }

        return new ReadResult([.. order.Select(f => new ColumnInfo(f, "json"))], rows, rows.Count, truncated);
    }

    static ReadResult Single(string column, string value)
    {
        var rows = new JsonArray();
        var row = new JsonArray();
        Append(row, JsonValue.Create(value));
        Append(rows, row);
        return new ReadResult([new ColumnInfo(column, "text")], rows, 1, false);
    }

    static void Append(JsonArray array, JsonNode? node) => ((IList<JsonNode?>)array).Add(node);

    static string Truncate(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    static AdapterException Translate(Exception ex) => ex switch
    {
        AdapterException adapter => adapter,
        TaskCanceledException or TimeoutException
            => new AdapterException(ErrorCodes.SourceTimeout, "the API did not answer in time", ex),
        HttpRequestException http
            => new AdapterException(ErrorCodes.SourceUnreachable, $"source is unreachable: {http.Message}", http),
        _ => new AdapterException(ErrorCodes.Internal, ex.Message, ex),
    };

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
