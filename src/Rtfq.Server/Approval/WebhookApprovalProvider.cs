using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rtfq.Server.Approval;

public sealed record WebhookApprovalRequest(
    string Source, string TokenId, string Target, string Kind,
    string Statement, int? AffectedRows, string DiffRows, string Fingerprint);

public sealed record WebhookApprovalAck(string RequestId);

public sealed record WebhookApprovalStatus(string State, string? Approver, string? Reason);

[JsonSerializable(typeof(WebhookApprovalRequest))]
[JsonSerializable(typeof(WebhookApprovalAck))]
[JsonSerializable(typeof(WebhookApprovalStatus))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal sealed partial class WebhookJson : JsonSerializerContext;

/// <summary>
/// Approval by HTTP callback: this is how Slack gets built without Slack living
/// in core.
///
/// It also answers the question CLAUDE.md left open about what a plugin means
/// here. NativeAOT rules out loading assemblies at runtime, so a plugin cannot be
/// a DLL dropped into a folder. A webhook is a boundary that survives AOT, keeps
/// our binary free of anyone else's SDK, and lets an integration be written in
/// whatever language its author prefers.
///
/// Failing closed is the whole contract: an endpoint that is down, slow, or
/// answering nonsense yields no approval, never a default one.
/// </summary>
public sealed class WebhookApprovalProvider : IApprovalProvider, IDisposable
{
    readonly HttpClient _http;
    readonly bool _ownsHttp;

    public string Name => "webhook";

    public WebhookApprovalProvider(Uri endpoint, TimeSpan timeout, IReadOnlyDictionary<string, string>? headers = null)
    {
        _http = new HttpClient { BaseAddress = endpoint, Timeout = timeout };
        foreach (var (key, value) in headers ?? new Dictionary<string, string>())
            _http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        _ownsHttp = true;
    }

    /// <summary>For tests and for hosts that manage their own client.</summary>
    public WebhookApprovalProvider(HttpClient http)
    {
        _http = http;
        _ownsHttp = false;
    }

    public async Task<string> RequestAsync(ApprovalContext context, CancellationToken cancellationToken)
    {
        var payload = new WebhookApprovalRequest(
            context.Source, context.TokenId, context.Target, context.Kind,
            context.Statement, context.AffectedRows, context.DiffRows, context.Fingerprint);

        using var response = await _http.PostAsJsonAsync(
            "requests", payload, WebhookJson.Default.WebhookApprovalRequest, cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

        var ack = await response.Content
            .ReadFromJsonAsync(WebhookJson.Default.WebhookApprovalAck, cancellationToken).ConfigureAwait(false);

        return ack?.RequestId ?? throw new InvalidOperationException("the approval endpoint returned no request id");
    }

    public async Task<ApprovalDecision> DecisionAsync(string requestId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.GetAsync($"requests/{Uri.EscapeDataString(requestId)}", cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
                return new ApprovalDecision(ApprovalState.Pending, null, $"endpoint returned {(int)response.StatusCode}");

            var status = await response.Content
                .ReadFromJsonAsync(WebhookJson.Default.WebhookApprovalStatus, cancellationToken).ConfigureAwait(false);

            return status?.State?.ToLowerInvariant() switch
            {
                "approved" => new ApprovalDecision(ApprovalState.Approved, status.Approver, status.Reason),
                "denied" => new ApprovalDecision(ApprovalState.Denied, status.Approver, status.Reason),
                "expired" => new ApprovalDecision(ApprovalState.Expired, status.Approver, status.Reason),
                // Anything unrecognised stays pending rather than becoming a yes.
                _ => new ApprovalDecision(ApprovalState.Pending, null, null),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Unreachable is not approved. It is also not an outright denial: the
            // handle expires on its own, and a flapping endpoint should not
            // destroy a proposal a human was about to accept.
            return new ApprovalDecision(ApprovalState.Pending, null, $"approval endpoint unreachable: {ex.Message}");
        }
    }

    public async Task WithdrawAsync(string requestId, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _http.DeleteAsync(
                $"requests/{Uri.EscapeDataString(requestId)}", cancellationToken).ConfigureAwait(false);
            _ = response.StatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // Best effort: withdrawing is a courtesy to the approver, not a gate.
        }
    }

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
    }
}
