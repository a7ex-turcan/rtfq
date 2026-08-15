using System.Text.Json.Nodes;
using Rtfq.Contracts;
using Rtfq.Client;
using Rtfq.Mcp;
using Rtfq.Server;
using Rtfq.Server.Configuration;

namespace Rtfq.Cli;

internal static class Program
{
    static readonly string[] Flags = ["production", "insecure-skip-verify", "help", "version", "quiet"];

    static async Task<int> Main(string[] argv)
    {
        var args = new Args(argv, Flags);

        if (args.Has("version"))
        {
            Console.WriteLine($"rtfq {RtfqVersion.Current}");
            return 0;
        }

        if (args.Command is "" or "help" || args.Has("help"))
        {
            PrintUsage();
            return args.Command is "" ? 2 : 0;
        }

        try
        {
            return args.Command switch
            {
                "serve" => await ServeAsync(args).ConfigureAwait(false),
                "validate" => Validate(args),
                "query" => await QueryAsync(args).ConfigureAwait(false),
                "sources" => await SourcesAsync(args).ConfigureAwait(false),
                "describe" => await DescribeAsync(args).ConfigureAwait(false),
                "refresh" => await RefreshAsync(args).ConfigureAwait(false),
                "explain" => await ExplainAsync(args).ConfigureAwait(false),
                "mcp" => await McpAsync(args).ConfigureAwait(false),
                _ => Fail($"unknown command '{args.Command}'"),
            };
        }
        catch (RtfqClientException ex)
        {
            // The server already said why in the stable taxonomy; print its code
            // rather than reinterpreting it.
            Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
            return 1;
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"error: cannot reach the server: {ex.Message}");
            return 1;
        }
    }

    // --- serve --------------------------------------------------------------

    static async Task<int> ServeAsync(Args args)
    {
        var (config, ok) = LoadAndValidate(args, out var production);
        if (!ok || config is null) return 1;

        var stateDir = StateDirectory.EnsureCreated(args.Value("state-dir"));
        if (Leftovers(args)) return 2;

        await using var server = await RtfqServer.StartAsync(config, stateDir).ConfigureAwait(false);

        Console.WriteLine($"rtfq {RtfqVersion.Current} listening on {server.BaseAddress}");
        Console.WriteLine($"  mode      {(production ? "production" : "development")}");
        Console.WriteLine($"  sources   {string.Join(", ", config.Sources.Select(s => $"{s.Name} ({s.Access.ToWire()})"))}");
        Console.WriteLine($"  audit     {Path.Combine(stateDir, "audit.jsonl")}");

        await server.WaitForShutdownAsync().ConfigureAwait(false);
        return 0;
    }

    // --- validate -------------------------------------------------------------

    static int Validate(Args args)
    {
        var (config, ok) = LoadAndValidate(args, out _);
        if (Leftovers(args)) return 2;
        if (!ok || config is null) return 1;

        Console.WriteLine($"config is valid: {config.Sources.Count} source(s), {config.Server.Auth.Tokens.Count} token(s)");
        return 0;
    }

    /// <summary>
    /// Loading and validating are separate passes, so <c>rtfq validate</c> can
    /// answer "is this safe to run?" without opening a listener or a connection.
    /// </summary>
    static (RtfqConfig? Config, bool Ok) LoadAndValidate(Args args, out bool production)
    {
        production = args.Has("production") ||
                     string.Equals(Environment.GetEnvironmentVariable("RTFQ_ENV"), "production", StringComparison.OrdinalIgnoreCase);

        var path = args.Value("config") ?? "rtfq.yaml";
        var load = ConfigLoader.LoadFile(path);

        foreach (var diagnostic in load.Diagnostics) Report(diagnostic, path);
        if (load.Config is null || load.HasErrors) return (null, false);

        var validation = ConfigValidator.Validate(load.Config, production);
        foreach (var diagnostic in validation.Diagnostics) Report(diagnostic, path);

        return (load.Config, !validation.HasErrors);
    }

    static void Report(Diagnostic d, string path)
    {
        var where = d.Line > 0 ? $"{path}:{d.Line}" : path;
        var stream = d.Severity == Severity.Error ? Console.Error : Console.Out;
        stream.WriteLine($"{(d.Severity == Severity.Error ? "error" : "warning")} [{d.Check}] {where}: {d.Message}");
    }

    // --- query ----------------------------------------------------------------

    static async Task<int> QueryAsync(Args args)
    {
        var source = args.Value("source");
        if (string.IsNullOrEmpty(source)) return Fail("--source is required");

        var maxRowsText = args.Value("max-rows");
        int? maxRows = null;
        if (maxRowsText is not null)
        {
            if (!int.TryParse(maxRowsText, out var parsed) || parsed <= 0) return Fail("--max-rows must be a positive integer");
            maxRows = parsed;
        }

        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var statement = string.Join(' ', args.Positional).Trim();
        if (statement.Length == 0) return Fail("a statement is required: rtfq query --source <name> \"SELECT ...\"");
        if (Leftovers(args)) return 2;

        var result = await client.QueryAsync(source, statement, maxRows).ConfigureAwait(false);
        PrintTable(result.Columns.Select(c => c.Name).ToList(), result.Rows);

        Console.WriteLine();
        Console.WriteLine(result.Truncated
            ? $"{result.RowCount} rows (TRUNCATED - more rows matched than the cap allows), {result.ElapsedMs} ms"
            : $"{result.RowCount} rows, {result.ElapsedMs} ms");
        return 0;
    }

    static async Task<int> SourcesAsync(Args args)
    {
        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);
        if (Leftovers(args)) return 2;

        var response = await client.ListSourcesAsync().ConfigureAwait(false);
        if (response.Sources.Count == 0)
        {
            Console.WriteLine("no sources are available to this token");
            return 0;
        }

        foreach (var s in response.Sources)
        {
            Console.WriteLine($"{s.Name}  [{s.Kind}]  access={s.EffectiveAccess} (source declares {s.Access})");
            if (s.Description.Length > 0) Console.WriteLine($"    {s.Description}");
        }
        return 0;
    }

    // --- discovery -------------------------------------------------------------

    static async Task<int> DescribeAsync(Args args)
    {
        var source = args.Value("source");
        if (string.IsNullOrEmpty(source)) return Fail("--source is required");

        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var table = args.Value("table") ?? args.Positional.FirstOrDefault();
        var pattern = args.Value("pattern");
        var limit = args.Value("limit");
        if (Leftovers(args)) return 2;

        if (table is not null)
        {
            Console.WriteLine(Render.Table(await client.DescribeTableAsync(source, table).ConfigureAwait(false)));
            return 0;
        }

        int? parsedLimit = int.TryParse(limit, out var l) ? l : null;
        Console.WriteLine(Render.Source(await client.DescribeSourceAsync(source, pattern, parsedLimit).ConfigureAwait(false)));
        return 0;
    }

    static async Task<int> RefreshAsync(Args args)
    {
        var source = args.Value("source") ?? args.Positional.FirstOrDefault();
        if (string.IsNullOrEmpty(source)) return Fail("a source is required: rtfq refresh <source>");

        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);
        if (Leftovers(args)) return 2;

        var result = await client.RefreshAsync(source).ConfigureAwait(false);
        Console.WriteLine($"{result.Source}: re-read {result.TableCount} table(s)");
        return 0;
    }

    static async Task<int> ExplainAsync(Args args)
    {
        var source = args.Value("source");
        if (string.IsNullOrEmpty(source)) return Fail("--source is required");

        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var statement = string.Join(' ', args.Positional).Trim();
        if (statement.Length == 0) return Fail("a statement is required");
        if (Leftovers(args)) return 2;

        var result = await client.ExplainAsync(source, statement).ConfigureAwait(false);
        Console.WriteLine(result.Plan);
        return 0;
    }

    /// <summary>
    /// Speaks MCP on stdio. stdout is the protocol channel from here on, so
    /// nothing may print to it but protocol messages — hence the banner on stderr.
    /// </summary>
    static async Task<int> McpAsync(Args args)
    {
        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);
        if (Leftovers(args)) return 2;

        Console.Error.WriteLine($"rtfq {RtfqVersion.Current} mcp server on stdio");

        var server = new McpServer(client, Console.In, Console.Out);
        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        try { await server.RunAsync(stopping.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* shutting down */ }
        return 0;
    }

    static RtfqClient? BuildClient(Args args, out string? error)
    {
        error = null;

        var server = args.Value("server")
                     ?? Environment.GetEnvironmentVariable("RTFQ_SERVER")
                     ?? "https://127.0.0.1:7420";

        var token = args.Value("token") ?? Environment.GetEnvironmentVariable("RTFQ_TOKEN");
        if (string.IsNullOrEmpty(token))
        {
            error = "no token: pass --token or set RTFQ_TOKEN";
            return null;
        }

        return new RtfqClient(server, token, args.Has("insecure-skip-verify"));
    }

    // --- output ------------------------------------------------------------------

    static void PrintTable(List<string> headers, JsonArray rows)
    {
        if (headers.Count == 0) { Console.WriteLine("(no columns)"); return; }

        var widths = headers.Select(h => h.Length).ToArray();
        var cells = new List<string[]>();

        foreach (var row in rows)
        {
            var values = new string[headers.Count];
            for (var i = 0; i < headers.Count; i++)
            {
                var cell = row is JsonArray array && i < array.Count ? array[i] : null;
                values[i] = cell?.ToString() ?? "NULL";
                widths[i] = Math.Max(widths[i], values[i].Length);
            }
            cells.Add(values);
        }

        // Keep one very wide column from destroying the layout of the rest.
        for (var i = 0; i < widths.Length; i++) widths[i] = Math.Min(widths[i], 60);

        Console.WriteLine(string.Join("  ", headers.Select((h, i) => h.PadRight(widths[i]))).TrimEnd());
        Console.WriteLine(string.Join("  ", widths.Select(w => new string('-', w))));

        foreach (var row in cells)
            Console.WriteLine(string.Join("  ", row.Select((v, i) => Truncate(v, widths[i]).PadRight(widths[i]))).TrimEnd());
    }

    static string Truncate(string value, int width) =>
        value.Length <= width ? value : value[..Math.Max(0, width - 1)] + "…";

    static bool Leftovers(Args args)
    {
        var unknown = args.Leftovers();
        if (unknown.Count == 0) return false;
        Console.Error.WriteLine($"error: unknown option(s): {string.Join(", ", unknown)}");
        return true;
    }

    static int Fail(string message)
    {
        Console.Error.WriteLine($"error: {message}");
        return 2;
    }

    static void PrintUsage()
    {
        Console.WriteLine($"""
            rtfq {RtfqVersion.Current} - governed, auditable access to your data sources

            USAGE
              rtfq serve     --config rtfq.yaml [--production] [--state-dir DIR]
              rtfq validate  --config rtfq.yaml [--production]
              rtfq sources
              rtfq describe  --source NAME [--table public.orders] [--pattern TEXT] [--limit N]
              rtfq refresh   NAME
              rtfq query     --source NAME "SELECT ..." [--max-rows N]
              rtfq explain   --source NAME "SELECT ..."
              rtfq mcp

            OPTIONS
              --config PATH             config file (default: rtfq.yaml)
              --production              treat inline secrets and missing TLS as errors, not warnings
              --state-dir DIR           where the audit log and schema cache live
              --server URL              server address (env: RTFQ_SERVER)
              --token TOKEN             bearer token (env: RTFQ_TOKEN)
              --max-rows N              lower the row cap for one query; it can never raise it
              --insecure-skip-verify    accept self-signed server certificates (development only)

            Reads are capped, discovery is served from a cache that survives the source
            going down, and every request is audited locally. Writes arrive in M3.
            """);
    }
}
