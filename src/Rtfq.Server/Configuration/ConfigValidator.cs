using System.Net;
using Rtfq.Adapters;
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
    // Which kinds exist and what they can do is the adapter layer's knowledge, not
    // the validator's. Hardcoding it here was a leak the M2 interface audit found:
    // adding an engine would have meant editing this file.

    public static ValidationResult Validate(RtfqConfig config, bool production)
    {
        var d = new List<Diagnostic>();

        ValidateListenAndTls(config, production, d);
        ValidateAuth(config, production, d);
        ValidateSources(config, production, d);
        ValidateApproval(config, production, d);

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

    /// <summary>
    /// An approval provider that cannot be reached approves nothing, so a
    /// misconfigured one must be caught here rather than at the moment somebody
    /// is waiting on it.
    /// </summary>
    static void ValidateApproval(RtfqConfig config, bool production, List<Diagnostic> d)
    {
        var a = config.Approval;
        var requiresApproval = config.Sources.Any(s => s.RequireApproval);

        if (string.Equals(a.Mode, "local", StringComparison.Ordinal))
        {
            if (a.Endpoint.Length > 0)
                d.Add(new("approval.endpoint_ignored", Severity.Warning,
                    "'approval.endpoint' is set but mode is local, so it is ignored", "approval.endpoint"));
            return;
        }

        if (!string.Equals(a.Mode, "webhook", StringComparison.Ordinal))
        {
            d.Add(new("approval.mode_unknown", Severity.Error,
                $"approval mode '{a.Mode}' must be local or webhook", "approval.mode"));
            return;
        }

        if (a.Endpoint.Length == 0)
        {
            d.Add(new("approval.endpoint_missing", Severity.Error,
                "webhook approval needs an 'approval.endpoint'", "approval.endpoint"));
        }
        else if (!Uri.TryCreate(a.Endpoint, UriKind.Absolute, out var uri) ||
                 (uri.Scheme != "http" && uri.Scheme != "https"))
        {
            d.Add(new("approval.endpoint_malformed", Severity.Error,
                $"'{a.Endpoint}' is not an absolute http(s) URL", "approval.endpoint"));
        }
        else if (uri.Scheme == "http" && production)
        {
            // The reply to this call decides whether a write happens. In the
            // clear, anyone on the path can be the approver.
            d.Add(new("approval.tls_in_production", Severity.Error,
                "the approval endpoint talks plain HTTP; production requires https", "approval.endpoint"));
        }

        if (a.HeadersHadInlineSecret)
        {
            d.Add(new("approval.header_inline_secret",
                production ? Severity.Error : Severity.Warning,
                "approval headers are written into the file; use env: or file: references instead",
                "approval.headers"));
        }

        if (!requiresApproval)
        {
            d.Add(new("approval.unused", Severity.Warning,
                "webhook approval is configured but no source sets require_approval, so it is never called",
                "approval.mode"));
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
            else if (!AdapterCatalog.IsKnown(source.Kind))
                d.Add(new("source.kind_supported", Severity.Error,
                    $"source kind '{source.Kind}' is not supported - available: {string.Join(", ", AdapterCatalog.Kinds.Order(StringComparer.Ordinal))}",
                    $"{path}.kind"));

            ValidateHttp(source, path, production, d);

            if (source.Kind == "http")
            {
                // An HTTP source has a base_url instead of a dsn.
            }
            else if (string.IsNullOrEmpty(source.Dsn))
                d.Add(new("source.dsn_present", Severity.Error,
                    $"source '{source.Name}' has no dsn", $"{path}.dsn"));
            else if (!source.DsnWasReference && SecretResolver.LooksLikeInlineSecret(source.Dsn))
                d.Add(new("source.dsn_not_inline",
                    production ? Severity.Error : Severity.Warning,
                    $"source '{source.Name}' has a password written into its dsn - use ${{env:...}} or ${{file:...}}",
                    $"{path}.dsn"));

            // ADR 0002: an adapter that cannot do transactional DDL may not be
            // marked access: schema. What each kind can do is asked of the adapter
            // layer rather than restated here.
            var declared = AdapterCatalog.DeclaredCapabilities(source.Kind);

            if (declared is not null && source.Access >= AccessLevel.Schema && !declared.TransactionalDdl)
            {
                d.Add(new("source.schema_requires_transactional_ddl", Severity.Error,
                    $"source '{source.Name}' is kind '{source.Kind}', which cannot roll back DDL, so it may not declare access: schema",
                    $"{path}.access"));
            }

            // Only fires for kinds that can NEVER do transactional writes. MongoDB
            // can, on a replica set, so its equivalent check needs a live
            // connection and happens at startup instead.
            if (declared is not null && source.Access >= AccessLevel.Write && !declared.TransactionalWrites)
            {
                d.Add(new("source.writes_require_transactions", Severity.Error,
                    $"source '{source.Name}' is kind '{source.Kind}', which has no transactions, so it may not be writable",
                    $"{path}.access"));
            }
        }

        if (config.Sources.Count == 0)
            d.Add(new("sources.present", Severity.Warning, "no sources are declared", "sources"));
    }

    static void ValidateHttp(SourceSection source, string path, bool production, List<Diagnostic> d)
    {
        if (source.Kind != "http") return;

        if (string.IsNullOrEmpty(source.BaseUrl))
        {
            d.Add(new("http.base_url_present", Severity.Error,
                $"source '{source.Name}' has no base_url", $"{path}.base_url"));
        }
        else if (!Uri.TryCreate(source.BaseUrl, UriKind.Absolute, out var uri))
        {
            d.Add(new("http.base_url_absolute", Severity.Error,
                $"base_url '{source.BaseUrl}' is not an absolute URL", $"{path}.base_url"));
        }
        else if (uri.Scheme == "http" && production)
        {
            d.Add(new("http.tls_in_production", Severity.Error,
                $"source '{source.Name}' talks plain HTTP; production requires https", $"{path}.base_url"));
        }

        if (source.AllowPaths.Count == 0)
        {
            d.Add(new("http.allow_paths_present", Severity.Error,
                $"source '{source.Name}' has no allow_paths, so it can reach nothing. State the paths explicitly.",
                $"{path}.allow_paths"));
        }

        // The rule CLAUDE.md calls out by name: a wildcard path combined with a
        // write method is a validation ERROR, not a warning. The two are harmless
        // apart and hand over the whole API together.
        var writeMethods = source.Methods
            .Where(m => !string.Equals(m, "GET", StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(m, "HEAD", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (writeMethods.Count > 0)
        {
            foreach (var wildcard in source.AllowPaths.Where(p => p.Contains('*', StringComparison.Ordinal)))
            {
                d.Add(new("http.wildcard_write", Severity.Error,
                    $"source '{source.Name}' allows {string.Join("/", writeMethods)} against the wildcard path " +
                    $"'{wildcard}'. A wildcard plus a write method hands over the whole API; list the paths explicitly.",
                    $"{path}.allow_paths"));
            }
        }

        foreach (var badPath in source.AllowPaths.Where(p => !p.StartsWith('/')))
        {
            d.Add(new("http.allow_path_rooted", Severity.Error,
                $"allow_path '{badPath}' must start with '/'", $"{path}.allow_paths"));
        }

        // At most one wildcard, and only at the very end. A pattern like
        // "/v1/*/invoices" reads as narrow and matches broadly, and "/v1/*/*" ends
        // in a star while still containing one in the middle — so counting matters
        // as much as position.
        foreach (var badPath in source.AllowPaths.Where(p =>
                     p.Count(c => c == '*') > 1 || (p.Contains('*', StringComparison.Ordinal) && !p.EndsWith('*'))))
        {
            d.Add(new("http.wildcard_suffix_only", Severity.Error,
                $"allow_path '{badPath}' may use at most one '*', and only as the final character",
                $"{path}.allow_paths"));
        }

        if (source.HeadersHadInlineSecret)
        {
            d.Add(new("http.header_not_inline",
                production ? Severity.Error : Severity.Warning,
                $"source '{source.Name}' has a header value written into the config - use ${{env:...}} or ${{file:...}}",
                $"{path}.headers"));
        }
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
