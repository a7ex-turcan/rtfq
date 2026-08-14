using System.Net;
using Rtfq.Contracts;

namespace Rtfq.Server.Configuration;

/// <summary>
/// A separate pass from loading, so <c>rtfq validate</c> can answer "is this
/// config safe to run?" without starting a listener or touching a database.
///
/// Every rule is a <b>named check</b>. Checks whose subject does not exist yet
/// are registered anyway and simply do not fire — the framework being in place
/// from M0 is the point, because a validation rule retrofitted in M2 across four
/// adapters is the expensive way to learn this.
/// </summary>
public static class ConfigValidator
{
    static readonly string[] SupportedKinds = ["postgres"];

    /// <summary>Kinds whose adapter can do transactional DDL, and so may declare access: schema (ADR 0002).</summary>
    static readonly string[] TransactionalDdlKinds = ["postgres", "mssql"];

    public static ValidationResult Validate(RtfqConfig config, bool production)
    {
        var d = new List<Diagnostic>();

        ValidateListenAndTls(config, production, d);
        ValidateAuth(config, production, d);
        ValidateSources(config, production, d);

        return new ValidationResult(d);
    }

    static void ValidateListenAndTls(RtfqConfig config, bool production, List<Diagnostic> d)
    {
        var listen = config.Server.Listen;
        if (!TryParseListen(listen, out var endpoint))
        {
            d.Add(new("server.listen.parseable", Severity.Error,
                $"'{listen}' is not a valid host:port", "server.listen"));
            return;
        }

        var loopback = IPAddress.IsLoopback(endpoint.Address);

        // A rule, not a config knob: TLS is mandatory the moment the listener is
        // reachable from another machine. There is no --insecure escape hatch.
        if (!loopback && config.Server.Tls is null)
        {
            d.Add(new("server.tls.required_unless_loopback", Severity.Error,
                $"listening on {listen} exposes the server beyond this machine, so 'server.tls' is required",
                "server.tls"));
        }

        if (config.Server.Tls is { } tls)
        {
            if (!File.Exists(tls.CertPath))
                d.Add(new("server.tls.files_exist", Severity.Error, $"TLS certificate '{tls.CertPath}' does not exist", "server.tls.cert"));
            if (!File.Exists(tls.KeyPath))
                d.Add(new("server.tls.files_exist", Severity.Error, $"TLS key '{tls.KeyPath}' does not exist", "server.tls.key"));
        }
        else if (production)
        {
            d.Add(new("server.tls.production", Severity.Error,
                "production mode requires TLS even on loopback", "server.tls"));
        }
    }

    static void ValidateAuth(RtfqConfig config, bool production, List<Diagnostic> d)
    {
        var auth = config.Server.Auth;

        if (!string.Equals(auth.Mode, "token", StringComparison.Ordinal))
            d.Add(new("auth.mode_supported", Severity.Error,
                $"auth mode '{auth.Mode}' is not supported - only 'token' exists today", "server.auth.mode"));

        if (auth.Tokens.Count == 0)
            d.Add(new("auth.tokens_present", Severity.Error,
                "at least one token is required - RTFQ never serves unauthenticated", "server.auth.tokens"));

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (token, i) in auth.Tokens.Select((t, i) => (t, i)))
        {
            var path = $"server.auth.tokens[{i}]";

            if (!seen.Add(token.Id))
                d.Add(new("auth.token.id_unique", Severity.Error, $"duplicate token id '{token.Id}'", path));

            if (string.IsNullOrEmpty(token.Secret))
            {
                d.Add(new("auth.token.secret_present", Severity.Error, $"token '{token.Id}' has no secret", $"{path}.secret"));
            }
            else if (!token.SecretWasReference)
            {
                // The dev/production split: an inline secret is a smell while you
                // are wiring things up, and disqualifying once real data is behind it.
                d.Add(new("auth.token.secret_not_inline",
                    production ? Severity.Error : Severity.Warning,
                    $"token '{token.Id}' has its secret written into the config - use ${{env:...}} or ${{file:...}}",
                    $"{path}.secret"));
            }

            foreach (var (sourceName, _) in token.Grants)
            {
                if (config.FindSource(sourceName) is null)
                    d.Add(new("auth.grant.source_exists", Severity.Error,
                        $"token '{token.Id}' is granted on '{sourceName}', which is not a declared source",
                        $"{path}.grants.{sourceName}"));
            }
        }
    }

    static void ValidateSources(RtfqConfig config, bool production, List<Diagnostic> d)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (source, i) in config.Sources.Select((s, i) => (s, i)))
        {
            var path = $"sources[{i}]";

            if (!seen.Add(source.Name))
                d.Add(new("source.name_unique", Severity.Error, $"duplicate source name '{source.Name}'", path));

            if (string.IsNullOrEmpty(source.Kind))
                d.Add(new("source.kind_supported", Severity.Error, $"source '{source.Name}' has no kind", $"{path}.kind"));
            else if (!SupportedKinds.Contains(source.Kind, StringComparer.Ordinal))
                d.Add(new("source.kind_supported", Severity.Error,
                    $"source kind '{source.Kind}' is not supported yet - M0 ships {string.Join(", ", SupportedKinds)}",
                    $"{path}.kind"));

            if (string.IsNullOrEmpty(source.Dsn))
                d.Add(new("source.dsn_present", Severity.Error,
                    $"source '{source.Name}' has no dsn", $"{path}.dsn"));
            else if (!source.DsnWasReference && SecretResolver.LooksLikeInlineSecret(source.Dsn))
                d.Add(new("source.dsn_not_inline",
                    production ? Severity.Error : Severity.Warning,
                    $"source '{source.Name}' has a password written into its dsn - use ${{env:...}} or ${{file:...}}",
                    $"{path}.dsn"));

            // ADR 0002: an adapter that cannot do transactional DDL may not be
            // marked access: schema. Registered now; fires the moment a Mongo or
            // HTTP source exists.
            if (source.Access == AccessLevel.Schema &&
                source.Kind.Length > 0 &&
                !TransactionalDdlKinds.Contains(source.Kind, StringComparer.Ordinal))
            {
                d.Add(new("source.schema_requires_transactional_ddl", Severity.Error,
                    $"source '{source.Name}' is kind '{source.Kind}', which cannot do transactional DDL, so it may not declare access: schema",
                    $"{path}.access"));
            }

            // Registered for M2, inert until those kinds load:
            //   source.mongo_standalone_not_writable
            //   source.http_wildcard_write
        }

        if (config.Sources.Count == 0)
            d.Add(new("sources.present", Severity.Warning, "no sources are declared", "sources"));
    }

    /// <summary>Parses <c>host:port</c>. Accepts bare IPv4/IPv6 and hostnames that resolve to a literal.</summary>
    public static bool TryParseListen(string listen, out IPEndPoint endpoint)
    {
        endpoint = new IPEndPoint(IPAddress.Loopback, 0);
        if (string.IsNullOrWhiteSpace(listen)) return false;

        var lastColon = listen.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == listen.Length - 1) return false;

        var host = listen[..lastColon].Trim('[', ']');
        var portText = listen[(lastColon + 1)..];

        // Port 0 is legitimate: it asks the OS for an ephemeral port, which is how
        // tests bind without racing each other for a fixed one.
        if (!int.TryParse(portText, out var port) || port is < 0 or > 65535) return false;

        if (!IPAddress.TryParse(host, out var address))
        {
            // "localhost" is the only name we resolve without DNS: treating an
            // unresolvable name as non-loopback would be the unsafe default.
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
                address = IPAddress.Loopback;
            else
                return false;
        }

        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
