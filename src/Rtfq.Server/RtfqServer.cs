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

    RtfqServer(WebApplication app, SourceRegistry sources, AuditLog audit)
    {
        _app = app;
        _sources = sources;
        _audit = audit;
    }

    /// <summary>The address actually bound, which matters when the config asked for port 0.</summary>
    public string BaseAddress =>
        _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?
            .Addresses.FirstOrDefault() ?? "";

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
        builder.Services.AddSingleton(provider => new SchemaCache(
            stateDirectory,
            config.Defaults.SchemaCacheTtl,
            sources,
            provider.GetRequiredService<ILogger<SchemaCache>>()));

        var app = builder.Build();
        ApiEndpoints.Map(app);

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

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
        await _sources.DisposeAsync().ConfigureAwait(false);
        _audit.Dispose();
    }
}
