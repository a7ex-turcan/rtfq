using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rtfq.Server.Audit;
using Rtfq.Server.Auth;
using Rtfq.Server.Configuration;
using Rtfq.Server.Endpoints;
using Rtfq.Server.Policy;
using Rtfq.Server.Schema;

namespace Rtfq.Server;

/// <summary>
/// Hosting for the HTTP+JSON server: the stable contract the CLI and the MCP
/// adapter are both clients of. Neither is privileged, and nothing here knows
/// either exists.
/// </summary>
public sealed class RtfqServer : IAsyncDisposable
{
    readonly WebApplication _app;
    readonly SourceRegistry _sources;
    readonly AuditLog _audit;
    readonly Approval.IApprovalProvider _approvals;

    RtfqServer(WebApplication app, SourceRegistry sources, AuditLog audit, Approval.IApprovalProvider approvals)
    {
        _app = app;
        _sources = sources;
        _audit = audit;
        _approvals = approvals;
    }

    /// <summary>The address actually bound, which matters when the config asked for port 0.</summary>
    public string BaseAddress =>
        _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?
            .Addresses.FirstOrDefault() ?? "";

    /// <param name="approvals">
    /// Overrides the provider the config asks for. Only tests pass this; a
    /// deployment selects its provider with the <c>approval:</c> section, and the
    /// broker cannot tell the difference either way.
    /// </param>
    public static async Task<RtfqServer> StartAsync(
        RtfqConfig config,
        string stateDirectory,
        Approval.IApprovalProvider? approvals = null,
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
                if (config.Server.Tls is { } tls) listen.UseHttps(LoadCertificate(tls));
            });
        });

        var audit = new AuditLog(stateDirectory);
        var sources = new SourceRegistry(config);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(audit);
        builder.Services.AddSingleton(sources);
        builder.Services.AddSingleton(new PolicyEngine(config));
        builder.Services.AddSingleton(new TokenAuthenticator(config));
        var unlocks = new Policy.UnlockStore();
        var approver = approvals ?? BuildApprovalProvider(config);

        builder.Services.AddSingleton(unlocks);
        builder.Services.AddSingleton(approver);

        // Registered concretely as well, because the approval *queue* endpoints
        // only exist for the local provider - a webhook keeps its own queue, and
        // answering it is that service's business rather than ours.
        if (approver is Approval.LocalApprovalProvider local) builder.Services.AddSingleton(local);

        builder.Services.AddSingleton(provider => new Broker.MutationBroker(
            config, sources, audit,
            provider.GetRequiredService<ILogger<Broker.MutationBroker>>(),
            approver, unlocks, startSweeper: true));
        builder.Services.AddSingleton(provider => new SchemaCache(
            stateDirectory,
            config.Defaults.SchemaCacheTtl,
            sources,
            provider.GetRequiredService<ILogger<SchemaCache>>()));

        var app = builder.Build();
        ApiEndpoints.Map(app);
        ApprovalEndpoints.Map(app);

        app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Rtfq.Startup")
            .LogInformation("Approvals go to the {Provider} provider", approver.Name);

        await CheckCapabilitiesAsync(app, config, sources, cancellationToken).ConfigureAwait(false);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        return new RtfqServer(app, sources, audit, approver);
    }

    /// <summary>
    /// Some declarations can only be checked against a live source: MongoDB does
    /// transactions on a replica set and not on a standalone, and the config
    /// cannot say which is out there. So <c>rtfq validate</c> stays offline and
    /// this runs at startup.
    ///
    /// A source that is simply unreachable is a warning, not a failure — refusing
    /// to start because one database is down would contradict the offline
    /// discovery this server is built around.
    /// </summary>
    static async Task CheckCapabilitiesAsync(
        WebApplication app, RtfqConfig config, SourceRegistry sources, CancellationToken cancellationToken)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Rtfq.Startup");

        var problems = await sources
            .CheckCapabilitiesAsync(config, TimeSpan.FromSeconds(10), cancellationToken)
            .ConfigureAwait(false);

        foreach (var problem in problems.Where(p => !p.Fatal))
            logger.LogWarning("Source {Source} {Message}", problem.Source, problem.Message);

        var fatal = problems.Where(p => p.Fatal).ToList();
        if (fatal.Count == 0) return;

        var detail = string.Join(Environment.NewLine,
            fatal.Select(p => $"  {p.Source}: {p.Message}"));

        throw new InvalidOperationException(
            $"refusing to start; {fatal.Count} source(s) declare access their deployment cannot support:{Environment.NewLine}{detail}");
    }

    static Approval.IApprovalProvider BuildApprovalProvider(RtfqConfig config)
    {
        if (!string.Equals(config.Approval.Mode, "webhook", StringComparison.Ordinal))
            return new Approval.LocalApprovalProvider(config.Defaults.ApprovalTtl);

        return new Approval.WebhookApprovalProvider(
            new Uri(config.Approval.Endpoint.EndsWith('/') ? config.Approval.Endpoint : config.Approval.Endpoint + "/"),
            config.Approval.Timeout,
            config.Approval.Headers);
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

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await _sources.DisposeAsync().ConfigureAwait(false);
        (_approvals as IDisposable)?.Dispose();
        _audit.Dispose();
    }
}
