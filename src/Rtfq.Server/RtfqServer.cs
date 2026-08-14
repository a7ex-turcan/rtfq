using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rtfq.Adapters;
using Rtfq.Contracts;
using Rtfq.Server.Audit;
using Rtfq.Server.Auth;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;

namespace Rtfq.Server;

/// <summary>
/// The HTTP+JSON server. This is the stable contract the whole system depends on:
/// the CLI is a client of it, and from M1 the MCP adapter is another. Neither is
/// privileged, and nothing here knows either exists.
/// </summary>
public sealed class RtfqServer : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly SourceRegistry _sources;
    readonly AuditLog _audit;

    RtfqServer(WebApplication app, SourceRegistry sources, AuditLog audit)
    {
        _app = app;
        _sources = sources;
        _audit = audit;
    }

    /// <summary>The address the server actually bound, which matters when the config asked for port 0.</summary>
    public string BaseAddress
    {
        get
        {
            var addresses = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
            return addresses?.Addresses.FirstOrDefault() ?? "";
        }
    }

    public static async Task<RtfqServer> StartAsync(
        RtfqConfig config,
        string stateDirectory,
        CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        if (!ConfigValidator.TryParseListen(config.Server.Listen, out var endpoint))
            throw new InvalidOperationException($"'{config.Server.Listen}' is not a valid listen address");

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(endpoint.Address, endpoint.Port, listen =>
            {
                if (config.Server.Tls is { } tls)
                    listen.UseHttps(LoadCertificate(tls));
            });
        });

        var audit = new AuditLog(stateDirectory);
        var sources = new SourceRegistry(config);
        var policy = new PolicyEngine(config);
        var authenticator = new TokenAuthenticator(config);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(audit);
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton(policy);
        builder.Services.AddSingleton(authenticator);

        var app = builder.Build();
        MapEndpoints(app);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new RtfqServer(app, sources, audit);
    }

    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) =>
        _app.WaitForShutdownAsync(cancellationToken);

    /// <summary>
    /// Kestrel cannot use a PEM-loaded certificate directly on Windows; round-trip
    /// through PKCS#12 so one config works on every platform.
    /// </summary>
    static X509Certificate2 LoadCertificate(TlsSection tls)
    {
        var pem = X509Certificate2.CreateFromPemFile(tls.CertPath, tls.KeyPath);
        if (!OperatingSystem.IsWindows()) return pem;

        var pfx = pem.Export(X509ContentType.Pfx);
        pem.Dispose();
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    // --- endpoints ---------------------------------------------------------
    //
    // Written as explicit HttpContext delegates with hand-called serialization
    // rather than parameter-inferred minimal APIs. Inference relies on generated
    // or reflected binding; doing it by hand keeps the whole request path
    // AOT-provable, which per ADR 0001 is the only version of the code that counts.

    static void MapEndpoints(WebApplication app)
    {
        app.MapGet("/health", async ctx =>
        {
            await WriteJson(ctx, StatusCodes.Status200OK,
                new HealthResponse("ok", RtfqVersion.Current), RtfqJson.Default.HealthResponse).ConfigureAwait(false);
        });

        app.MapGet("/v1/sources", async ctx =>
        {
            var request = RequestScope.Begin(ctx);
            var caller = Authenticate(ctx, request);
            if (caller is null) return;

            var policy = ctx.RequestServices.GetRequiredService<PolicyEngine>();
            var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();

            var list = new List<SourceInfo>();
            foreach (var (source, effective) in policy.VisibleSources(caller))
            {
                var capabilities = registry.TryGet(source.Name, out var adapter)
                    ? adapter.Capabilities.ToWire()
                    : [];

                list.Add(new SourceInfo
                {
                    Name = source.Name,
                    Kind = source.Kind,
                    Description = source.Description,
                    Access = source.Access.ToWire(),
                    EffectiveAccess = effective.ToWire(),
                    Capabilities = capabilities,
                });
            }

            request.Audit(ctx, "list_sources", caller.TokenId, null, null, "read", "ok", null, null, null);
            await WriteJson(ctx, StatusCodes.Status200OK,
                new SourcesResponse(list), RtfqJson.Default.SourcesResponse).ConfigureAwait(false);
        });

        app.MapPost("/v1/query", async ctx =>
        {
            var request = RequestScope.Begin(ctx);
            var caller = Authenticate(ctx, request);
            if (caller is null) return;

            QueryRequest? body;
            try
            {
                body = await JsonSerializer.DeserializeAsync(
                    ctx.Request.Body, RtfqJson.Default.QueryRequest, ctx.RequestAborted).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                await Refuse(ctx, request, caller.TokenId, null, null,
                    StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed, ex.Message).ConfigureAwait(false);
                return;
            }

            if (body is null || string.IsNullOrWhiteSpace(body.Source))
            {
                await Refuse(ctx, request, caller.TokenId, null, null,
                    StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                    "body must be {\"source\": \"...\", \"statement\": \"...\"}").ConfigureAwait(false);
                return;
            }

            if (string.IsNullOrWhiteSpace(body.Statement))
            {
                await Refuse(ctx, request, caller.TokenId, body.Source, null,
                    StatusCodes.Status400BadRequest, ErrorCodes.StatementEmpty,
                    "statement is empty").ConfigureAwait(false);
                return;
            }

            var policy = ctx.RequestServices.GetRequiredService<PolicyEngine>();
            var decision = policy.Evaluate(caller, body.Source, AccessLevel.Read);
            if (!decision.Allowed)
            {
                var status = decision.Outcome == Outcome.SourceUnknown
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status403Forbidden;

                await Refuse(ctx, request, caller.TokenId, body.Source, body.Statement,
                    status, decision.ErrorCode!, decision.Message).ConfigureAwait(false);
                return;
            }

            var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
            var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();
            var source = config.FindSource(body.Source)!;

            // The caller may lower its own ceiling but never raise it.
            var configured = source.EffectiveMaxRows(config.Defaults);
            var maxRows = body.MaxRows is { } requested && requested > 0
                ? Math.Min(requested, configured)
                : configured;

            try
            {
                var adapter = registry[body.Source];
                var result = await adapter.ExecuteReadAsync(
                    body.Statement,
                    new ReadOptions(maxRows, source.EffectiveStatementTimeout(config.Defaults)),
                    ctx.RequestAborted).ConfigureAwait(false);

                request.Audit(ctx, "query", caller.TokenId, body.Source, body.Statement,
                    "read", "ok", null, result.RowCount, result.Truncated);

                await WriteJson(ctx, StatusCodes.Status200OK, new QueryResponse
                {
                    Columns = result.Columns,
                    Rows = result.Rows,
                    RowCount = result.RowCount,
                    Truncated = result.Truncated,
                    ElapsedMs = request.ElapsedMs,
                    NextCursor = null,
                }, RtfqJson.Default.QueryResponse).ConfigureAwait(false);
            }
            catch (AdapterException ex)
            {
                var status = ex.ErrorCode switch
                {
                    ErrorCodes.SourceRejected => StatusCodes.Status400BadRequest,
                    ErrorCodes.SourceTimeout => StatusCodes.Status504GatewayTimeout,
                    ErrorCodes.SourceUnreachable => StatusCodes.Status502BadGateway,
                    _ => StatusCodes.Status500InternalServerError,
                };
                await Refuse(ctx, request, caller.TokenId, body.Source, body.Statement,
                    status, ex.ErrorCode, ex.Message).ConfigureAwait(false);
            }
        });
    }

    static Caller? Authenticate(HttpContext ctx, RequestScope request)
    {
        var authenticator = ctx.RequestServices.GetRequiredService<TokenAuthenticator>();
        var presented = TokenAuthenticator.ExtractBearer(ctx.Request.Headers.Authorization);

        if (presented is null)
        {
            RefuseSync(ctx, request, null, null, null,
                StatusCodes.Status401Unauthorized, ErrorCodes.TokenMissing,
                "an Authorization: Bearer <token> header is required");
            return null;
        }

        var caller = authenticator.Authenticate(presented);
        if (caller is null)
        {
            RefuseSync(ctx, request, null, null, null,
                StatusCodes.Status401Unauthorized, ErrorCodes.TokenInvalid, "the presented token is not recognised");
            return null;
        }

        return caller;
    }

    // Refusals are audited exactly like successes. "Every request, including
    // refusals" is what makes the log answer the question a security review asks.
    static async Task Refuse(HttpContext ctx, RequestScope request, string? tokenId, string? source,
        string? statement, int status, string code, string message)
    {
        request.Audit(ctx, OperationOf(ctx), tokenId, source, statement, "refused", "error", code, null, null);
        await WriteJson(ctx, status, new ErrorResponse(new ErrorBody(code, message)), RtfqJson.Default.ErrorResponse)
            .ConfigureAwait(false);
    }

    static void RefuseSync(HttpContext ctx, RequestScope request, string? tokenId, string? source,
        string? statement, int status, string code, string message)
        => Refuse(ctx, request, tokenId, source, statement, status, code, message).GetAwaiter().GetResult();

    static string OperationOf(HttpContext ctx) =>
        ctx.Request.Path.Value?.Contains("query", StringComparison.Ordinal) == true ? "query" : "list_sources";

    static async Task WriteJson<T>(HttpContext ctx, int status, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo)
    {
        ctx.Response.StatusCode = status;
        ctx.Response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(ctx.Response.Body, value, typeInfo, ctx.RequestAborted).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await _sources.DisposeAsync().ConfigureAwait(false);
        _audit.Dispose();
    }
}

/// <summary>Per-request identity and timing, so every audit line can be tied to one call.</summary>
internal sealed class RequestScope
{
    readonly long _startedAt = Stopwatch.GetTimestamp();

    public required string Id { get; init; }

    public long ElapsedMs => (long)Stopwatch.GetElapsedTime(_startedAt).TotalMilliseconds;

    public static RequestScope Begin(HttpContext ctx)
    {
        var scope = new RequestScope { Id = Guid.NewGuid().ToString("n")[..12] };
        ctx.Response.Headers["X-Request-Id"] = scope.Id;
        return scope;
    }

    public void Audit(HttpContext ctx, string operation, string? tokenId, string? source, string? statement,
        string classification, string outcome, string? errorCode, int? rowCount, bool? truncated)
    {
        ctx.RequestServices.GetRequiredService<AuditLog>().Write(new AuditEntry
        {
            RequestId = Id,
            Operation = operation,
            TokenId = tokenId,
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
}
