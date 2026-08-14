using Rtfq.Contracts;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace Rtfq.Server.Configuration;

public sealed record LoadResult(RtfqConfig? Config, IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool HasErrors => Diagnostics.Any(d => d.Severity == Severity.Error);
}

/// <summary>
/// Loads YAML into the typed model by hand, walking YamlDotNet's node model
/// rather than using its reflection-based deserializer.
///
/// Two reasons, both load-bearing. Reflection deserialization does not survive
/// trimming under NativeAOT (ADR 0001). And hand-mapping is what lets a config
/// mistake be reported as "line 14: unknown key 'acess'" instead of a type-load
/// exception — for a file that decides who may write to production, the quality
/// of that error message is a feature.
/// </summary>
public static class ConfigLoader
{
    public static LoadResult LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            return new(null, [new Diagnostic("config.file_missing", Severity.Error, $"config file '{path}' does not exist")]);
        }

        string text;
        try { text = File.ReadAllText(path); }
        catch (IOException ex)
        {
            return new(null, [new Diagnostic("config.file_unreadable", Severity.Error, ex.Message)]);
        }

        return LoadText(text);
    }

    public static LoadResult LoadText(string text)
    {
        var diags = new List<Diagnostic>();

        YamlMappingNode root;
        try
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(text));
            if (stream.Documents.Count == 0)
            {
                diags.Add(new("config.empty", Severity.Error, "config file is empty"));
                return new(null, diags);
            }
            if (stream.Documents[0].RootNode is not YamlMappingNode map)
            {
                diags.Add(new("config.malformed", Severity.Error, "top level of the config must be a mapping"));
                return new(null, diags);
            }
            root = map;
        }
        catch (YamlException ex)
        {
            diags.Add(new("config.malformed", Severity.Error, ex.Message, "", (int)ex.Start.Line));
            return new(null, diags);
        }

        var cursor = new MapCursor(root, "", diags);

        var server = ReadServer(cursor.Map("server"), diags);
        var defaults = ReadDefaults(cursor.Map("defaults"), diags);
        var sources = ReadSources(cursor.Seq("sources"), diags);
        cursor.ReportUnknownKeys();

        if (server is null)
        {
            diags.Add(new("config.server_missing", Severity.Error, "a 'server' section is required"));
            return new(null, diags);
        }

        var config = new RtfqConfig { Server = server, Defaults = defaults, Sources = sources };
        return new(config, diags);
    }

    // --- sections ---------------------------------------------------------

    static ServerSection? ReadServer(MapCursor? c, List<Diagnostic> diags)
    {
        if (c is null) return null;

        var listen = c.Secret("listen", out _) ?? "127.0.0.1:7420";

        TlsSection? tls = null;
        if (c.Map("tls") is { } tlsMap)
        {
            var cert = tlsMap.Secret("cert", out _);
            var key = tlsMap.Secret("key", out _);
            tlsMap.ReportUnknownKeys();
            if (cert is null || key is null)
                diags.Add(new("server.tls.incomplete", Severity.Error, "tls needs both 'cert' and 'key'", tlsMap.Path, tlsMap.Line));
            else
                tls = new TlsSection { CertPath = cert, KeyPath = key };
        }

        var auth = ReadAuth(c.Map("auth"), diags)
                   ?? new AuthSection { Mode = "token", Tokens = [] };

        c.ReportUnknownKeys();
        return new ServerSection { Listen = listen, Tls = tls, Auth = auth };
    }

    static AuthSection? ReadAuth(MapCursor? c, List<Diagnostic> diags)
    {
        if (c is null)
        {
            diags.Add(new("auth.missing", Severity.Error, "a 'server.auth' section is required - RTFQ never listens unauthenticated"));
            return null;
        }

        var mode = c.Secret("mode", out _) ?? "token";
        var tokens = new List<TokenSection>();

        foreach (var (item, index) in c.SeqItems("tokens"))
        {
            var path = $"server.auth.tokens[{index}]";
            if (item is not YamlMappingNode m) continue;
            var t = new MapCursor(m, path, diags);

            var id = t.Secret("id", out _);
            var secret = t.Secret("secret", out var secretWasRef);

            var grants = new Dictionary<string, AccessLevel>(StringComparer.Ordinal);
            if (t.Map("grants") is { } g)
            {
                foreach (var (key, value) in g.Entries())
                {
                    if (!AccessLevels.TryParse(value?.ToString(), out var level))
                        diags.Add(new("auth.grant.level_unknown", Severity.Error,
                            $"grant '{value}' for source '{key}' must be read, write or schema", $"{path}.grants.{key}", g.Line));
                    grants[key] = level;
                }
                g.MarkAllUsed();
            }
            t.ReportUnknownKeys();

            if (string.IsNullOrEmpty(id))
            {
                diags.Add(new("auth.token.id_missing", Severity.Error, "token needs an 'id'", path, t.Line));
                continue;
            }

            tokens.Add(new TokenSection
            {
                Id = id,
                Secret = secret ?? "",
                SecretWasReference = secretWasRef,
                Grants = grants,
            });
        }

        c.ReportUnknownKeys();
        return new AuthSection { Mode = mode, Tokens = tokens };
    }

    static DefaultsSection ReadDefaults(MapCursor? c, List<Diagnostic> diags)
    {
        var d = new DefaultsSection();
        if (c is null) return d;

        d = d with
        {
            MaxRows = c.Int("max_rows", d.MaxRows, diags),
            MaxAffectedRows = c.Int("max_affected_rows", d.MaxAffectedRows, diags),
            StatementTimeout = c.Duration("statement_timeout", d.StatementTimeout, diags),
            LockTimeout = c.Duration("lock_timeout", d.LockTimeout, diags),
            WriteHandleTtl = c.Duration("write_handle_ttl", d.WriteHandleTtl, diags),
        };
        c.ReportUnknownKeys();
        return d;
    }

    static List<SourceSection> ReadSources(MapCursor? c, List<Diagnostic> diags)
    {
        var sources = new List<SourceSection>();
        if (c is null) return sources;

        foreach (var (item, index) in c.Items())
        {
            var path = $"sources[{index}]";
            if (item is not YamlMappingNode m)
            {
                diags.Add(new("source.malformed", Severity.Error, "each source must be a mapping", path));
                continue;
            }

            var s = new MapCursor(m, path, diags);
            var name = s.Secret("name", out _);
            var kind = s.Secret("kind", out _);

            // A DSN may be spelled dsn (postgres/mssql) or uri (mongo).
            var dsn = s.Secret("dsn", out var dsnRef1) ?? s.Secret("uri", out dsnRef1);
            var dsnWasRef = dsnRef1;

            var accessText = s.Secret("access", out _);
            if (!AccessLevels.TryParse(accessText, out var access))
                diags.Add(new("source.access_unknown", Severity.Error,
                    $"access '{accessText}' must be read, write or schema", $"{path}.access", s.Line));

            var schemas = s.StringList("schemas");
            var maxRows = s.NullableInt("max_rows", diags);
            var timeout = s.NullableDuration("statement_timeout", diags);
            var description = s.Secret("description", out _) ?? "";

            // Consume keys that later milestones own, so M0 does not reject a
            // config written against the documented shape.
            s.Reserve("databases", "require_approval", "max_affected_rows", "deny_tables",
                      "writable_tables", "base_url", "methods", "allow_paths", "headers");
            s.ReportUnknownKeys();

            if (string.IsNullOrEmpty(name))
            {
                diags.Add(new("source.name_missing", Severity.Error, "source needs a 'name'", path, s.Line));
                continue;
            }

            sources.Add(new SourceSection
            {
                Name = name,
                Kind = kind ?? "",
                Dsn = dsn ?? "",
                DsnWasReference = dsnWasRef,
                Description = description,
                Access = access,
                Schemas = schemas,
                MaxRows = maxRows,
                StatementTimeout = timeout,
            });
        }

        return sources;
    }
}
