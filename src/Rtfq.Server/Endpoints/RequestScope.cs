using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rtfq.Contracts;
using Rtfq.Server.Audit;
using Rtfq.Server.Auth;
using Rtfq.Server.Policy;

namespace Rtfq.Server.Endpoints;

/// <summary>
/// Per-request identity, timing and the shared shape of a response.
///
/// Every path through the server ends in <see cref="Ok"/> or <see cref="Refuse"/>,
/// and both audit. That is what makes "every request, including refusals" true by
/// construction rather than by everyone remembering.
/// </summary>
internal sealed class RequestScope
{
    readonly long _startedAt = Stopwatch.GetTimestamp();

    public required string Id { get; init; }
    public required string Operation { get; init; }
    public HttpContext Context { get; private init; } = null!;

    public string? TokenId { get; private set; }
    public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    public static RequestScope Begin(HttpContext context, string operation)
    {
        var scope = new RequestScope
        {
            Id = Guid.NewGuid().ToString("n")[..12],
            Operation = operation,
            Context = context,
        };
        context.Response.Headers["X-Request-Id"] = scope.Id;
        return scope;
    }

    /// <summary>Authenticates, or writes a 401 and returns null.</summary>
    public async Task<Caller?> AuthenticateAsync()
    {
        var authenticator = Context.RequestServices.GetRequiredService<TokenAuthenticator>();
        var presented = TokenAuthenticator.ExtractBearer(Context.Request.Headers.Authorization);

        if (presented is null)
        {
            await Refuse(StatusCodes.Status401Unauthorized, ErrorCodes.TokenMissing,
                "an Authorization: Bearer <token> header is required").ConfigureAwait(false);
            return null;
        }

        var caller = authenticator.Authenticate(presented);
        if (caller is null)
        {
            await Refuse(StatusCodes.Status401Unauthorized, ErrorCodes.TokenInvalid,
                "the presented token is not recognised").ConfigureAwait(false);
            return null;
        }

        TokenId = caller.TokenId;
        return caller;
    }

    /// <summary>
    /// Resolves the source this caller may reach, or writes the refusal.
    /// A source with no grant and a source that does not exist produce the same
    /// answer, so an unauthorised caller cannot enumerate the estate.
    /// </summary>
    public async Task<bool> AuthoriseAsync(Caller caller, string source, AccessLevel required)
    {
        var policy = Context.RequestServices.GetRequiredService<PolicyEngine>();
        var decision = policy.Evaluate(caller, source, required);
        if (decision.Allowed) return true;

        var status = decision.Outcome == Outcome.SourceUnknown
            ? StatusCodes.Status404NotFound
            : StatusCodes.Status403Forbidden;

        await Refuse(status, decision.ErrorCode!, decision.Message, source).ConfigureAwait(false);
        return false;
    }

    public async Task Ok<T>(T value, JsonTypeInfo<T> typeInfo, string? source = null,
        string? statement = null, string classification = "read", int? rowCount = null, bool? truncated = null)
    {
        Audit(source, statement, classification, "ok", null, rowCount, truncated);
        await WriteAsync(StatusCodes.Status200OK, value, typeInfo).ConfigureAwait(false);
    }

    /// <param name="detail">What to do next, when there is something useful to say. Never a restatement of the message.</param>
    public async Task Refuse(
        int status, string code, string message,
        string? source = null, string? statement = null, string? detail = null)
    {
        Audit(source, statement, "refused", "error", code, null, null);
        await WriteAsync(status, new ErrorResponse(new ErrorBody(code, message, detail)), RtfqJson.Default.ErrorResponse)
            .ConfigureAwait(false);
    }

    /// <summary>Maps an adapter failure onto a status code without the handler knowing any driver specifics.</summary>
    /// <param name="diagnosis">
    /// What the caller should do next, when there is something useful to say.
    /// Carried in the error's detail rather than folded into the message, so a
    /// client can show or suppress it without parsing prose.
    /// </param>
    public Task RefuseAdapter(
        Rtfq.Adapters.AdapterException ex, string? source = null, string? statement = null, string? diagnosis = null)
    {
        var status = ex.ErrorCode switch
        {
            ErrorCodes.SourceRejected => StatusCodes.Status400BadRequest,
            ErrorCodes.StatementEmpty => StatusCodes.Status400BadRequest,
            ErrorCodes.InsufficientAccess => StatusCodes.Status403Forbidden,
            ErrorCodes.SourceUnknown => StatusCodes.Status404NotFound,
            ErrorCodes.SourceTimeout => StatusCodes.Status504GatewayTimeout,
            ErrorCodes.SourceUnreachable => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        };
        return Refuse(status, ex.ErrorCode, ex.Message, source, statement, diagnosis);
    }

    bool _audited;

    /// <summary>
    /// One audit line per request. Guarded rather than trusted: a handler that
    /// audits and then calls <see cref="Ok"/> would otherwise write two entries
    /// for one call, and the second — being the last — is what a reader would
    /// believe.
    /// </summary>
    public void Audit(string? source, string? statement, string classification, string outcome,
        string? errorCode, int? rowCount, bool? truncated)
    {
        if (_audited) return;
        _audited = true;

        Context.RequestServices.GetRequiredService<AuditLog>().Write(new AuditEntry
        {
            RequestId = Id,
            Operation = Operation,
            TokenId = TokenId,
            Source = source,
            Statement = statement,
            Classification = classification,
            Outcome = outcome,
            ErrorCode = errorCode,
            RowCount = rowCount,
            Truncated = truncated,
            ElapsedMs = ElapsedMs,
        });
    }

    async Task WriteAsync<T>(int status, T value, JsonTypeInfo<T> typeInfo)
    {
        Context.Response.StatusCode = status;
        Context.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(Context.Response.Body, value, typeInfo, Context.RequestAborted)
            .ConfigureAwait(false);
    }

    public string? Route(string key) => Context.Request.RouteValues.TryGetValue(key, out var v) ? v?.ToString() : null;

    public string? Query(string key) => Context.Request.Query.TryGetValue(key, out var v) ? v.ToString() : null;

    public int? QueryInt(string key) =>
        int.TryParse(Query(key), out var value) && value > 0 ? value : null;

    /// <summary>Reads and validates a JSON body, or writes a 400 and returns null.</summary>
    public async Task<T?> ReadBodyAsync<T>(JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync(Context.Request.Body, typeInfo, Context.RequestAborted)
                .ConfigureAwait(false);

            if (body is null)
            {
                await Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed, "a JSON body is required")
                    .ConfigureAwait(false);
            }
            return body;
        }
        catch (JsonException ex)
        {
            await Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed, ex.Message).ConfigureAwait(false);
            return null;
        }
    }
}
