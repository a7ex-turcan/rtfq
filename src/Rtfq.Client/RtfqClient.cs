using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Rtfq.Contracts;

namespace Rtfq.Client;

/// <summary>A refusal the server described in the stable error taxonomy.</summary>
public sealed class RtfqClientException(string code, string message, int statusCode)
    : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

/// <summary>
/// Thin client over the HTTP+JSON API. The CLI uses this; from M1 the MCP adapter
/// uses it too. It holds no policy and makes no decisions — everything that could
/// be called a rule lives on the server, where it cannot be bypassed by talking
/// to the port directly.
/// </summary>
public sealed class RtfqClient : IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsHttp;

    public RtfqClient(string baseAddress, string token, bool skipCertificateValidation = false)
    {
        var handler = new HttpClientHandler();
        if (skipCertificateValidation)
        {
            // Development affordance for self-signed certificates. Client-side
            // only: it cannot weaken anything the server enforces.
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }

        _http = new HttpClient(handler) { BaseAddress = new Uri(baseAddress, UriKind.Absolute) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _ownsHttp = true;
    }

    public RtfqClient(HttpClient http, string token)
    {
        _http = http;
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _ownsHttp = false;
    }

    public async Task<SourcesResponse> ListSourcesAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/v1/sources", cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.SourcesResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryResponse> QueryAsync(
        string source, string statement, int? maxRows = null, CancellationToken cancellationToken = default)
    {
        var request = new QueryRequest { Source = source, Statement = statement, MaxRows = maxRows };

        using var response = await _http.PostAsJsonAsync(
            "/v1/query", request, RtfqJson.Default.QueryRequest, cancellationToken).ConfigureAwait(false);

        return await ReadAsync(response, RtfqJson.Default.QueryResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DescribeSourceResponse> DescribeSourceAsync(
        string source, string? pattern = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(pattern)) query.Add("pattern=" + Uri.EscapeDataString(pattern));
        if (limit is { } l) query.Add("limit=" + l);
        var suffix = query.Count > 0 ? "?" + string.Join('&', query) : "";

        using var response = await _http.GetAsync($"/v1/sources/{Uri.EscapeDataString(source)}{suffix}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.DescribeSourceResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DescribeTableResponse> DescribeTableAsync(
        string source, string table, CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(
            $"/v1/sources/{Uri.EscapeDataString(source)}/tables/{Uri.EscapeDataString(table)}", cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.DescribeTableResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<DescribeSourceResponse> RefreshAsync(string source, CancellationToken cancellationToken = default)
    {
        using var response = await _http.PostAsync(
            $"/v1/sources/{Uri.EscapeDataString(source)}/refresh", content: null, cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.DescribeSourceResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<QueryResponse> SampleAsync(
        string source, string table, int? rows = null, CancellationToken cancellationToken = default)
    {
        var request = new SampleRequest { Source = source, Table = table, Rows = rows };
        using var response = await _http.PostAsJsonAsync("/v1/sample", request, RtfqJson.Default.SampleRequest, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.QueryResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExplainResponse> ExplainAsync(
        string source, string statement, CancellationToken cancellationToken = default)
    {
        var request = new ExplainRequest { Source = source, Statement = statement };
        using var response = await _http.PostAsJsonAsync("/v1/explain", request, RtfqJson.Default.ExplainRequest, cancellationToken)
            .ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.ExplainResponse, cancellationToken).ConfigureAwait(false);
    }

    public async Task<HealthResponse> HealthAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("/health", cancellationToken).ConfigureAwait(false);
        return await ReadAsync(response, RtfqJson.Default.HealthResponse, cancellationToken).ConfigureAwait(false);
    }

    static async Task<T> ReadAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            try
            {
                var error = await response.Content
                    .ReadFromJsonAsync(RtfqJson.Default.ErrorResponse, cancellationToken).ConfigureAwait(false);

                if (error?.Error is { } body)
                    throw new RtfqClientException(body.Code, body.Message, status);
            }
            catch (JsonException)
            {
                // Fall through: a non-JSON body means something other than RTFQ answered.
            }

            throw new RtfqClientException(ErrorCodes.Internal, $"server returned {status}", status);
        }

        var value = await response.Content.ReadFromJsonAsync(typeInfo, cancellationToken).ConfigureAwait(false);
        return value ?? throw new RtfqClientException(ErrorCodes.Internal, "server returned an empty body", 200);
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
