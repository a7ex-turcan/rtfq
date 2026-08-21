using System.Text.Json.Nodes;
using Rtfq.Contracts;
using Rtfq.Client;
using Rtfq.Mcp;
using Rtfq.Server;
using Rtfq.Server.Configuration;

namespace Rtfq.Cli;

internal static class Program
{
    static readonly string[] Flags =
        ["production", "insecure-skip-verify", "help", "version", "quiet", "write", "schema", "watch"];

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
                "approvals" => await ApprovalsAsync(args).ConfigureAwait(false),
                "unlock" => await UnlockAsync(args).ConfigureAwait(false),
                "lock" => await LockAsync(args).ConfigureAwait(false),
                "mcp" => await McpAsync(args).ConfigureAwait(false),
                _ => Fail($"unknown command '{args.Command}'"),
            };
        }
        catch (RtfqClientException ex)
        {
            // The server already said why in the stable taxonomy; print its code
            // rather than reinterpreting it.
            Console.Error.WriteLine($"error [{ex.Code}]: {ex.Message}");
            if (ex.Detail is { } detail) Console.Error.WriteLine($"  {detail}");
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

    // --- the human side of the write path -------------------------------------

    /// <summary>
    /// Shows what is waiting on a human, and records the answer.
    ///
    /// It prints the statement and the rows, and nothing else. There is no
    /// summary of what the change is "for", because the case this gate exists for
    /// is an agent persuaded by a poisoned row, and such an agent writes a very
    /// convincing summary.
    /// </summary>
    static async Task<int> ApprovalsAsync(Args args)
    {
        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var approve = args.Value("approve");
        var deny = args.Value("deny");
        var approver = args.Value("as") ?? Environment.UserName;
        var reason = args.Value("reason");
        var watch = args.Has("watch");
        if (Leftovers(args)) return 2;

        if (approve is not null || deny is not null)
        {
            var id = approve ?? deny!;
            var result = await client.DecideApprovalAsync(id, approve is not null, approver, reason)
                .ConfigureAwait(false);
            Console.WriteLine($"{result.Id}: {result.Outcome} by {approver}");
            return 0;
        }

        if (watch) return await WatchApprovalsAsync(client, approver).ConfigureAwait(false);

        var pending = await client.ListApprovalsAsync().ConfigureAwait(false);
        if (pending.Pending.Count == 0)
        {
            Console.WriteLine("nothing is waiting for approval");
            return 0;
        }

        foreach (var item in pending.Pending) PrintApproval(item);

        Console.WriteLine(new string('-', 72));
        return 0;
    }

    /// <summary>
    /// Stays open and reports approvals as they arrive.
    ///
    /// This exists because the local provider is a queue and not an inbox:
    /// nothing notifies anybody, so without a terminal left open somewhere, a
    /// proposal waits until it lapses and the person who could have approved it
    /// never knew. A webhook provider is the answer for a team; this is the
    /// answer for one operator at a desk.
    ///
    /// Polls rather than long-polls. The server holds no subscription state, an
    /// approval window is minutes long, and two seconds of latency on a decision
    /// a human is about to spend thirty seconds reading is not the bottleneck.
    /// </summary>
    static async Task<int> WatchApprovalsAsync(RtfqClient client, string approver)
    {
        // Interactive only when there is somebody to answer. Piped into a file
        // or a service manager, prompting would block forever on a read that
        // never returns.
        var interactive = !Console.IsInputRedirected;

        using var stopping = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopping.Cancel(); };

        Console.WriteLine(interactive
            ? $"watching for approvals as {approver}. a=approve, d=deny, s=skip. ctrl-c to stop."
            : "watching for approvals. ctrl-c to stop.");

        var seen = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            while (!stopping.IsCancellationRequested)
            {
                PendingApprovalsResponse pending;
                try
                {
                    pending = await client.ListApprovalsAsync(stopping.Token).ConfigureAwait(false);
                }
                catch (HttpRequestException ex)
                {
                    // A server restart should not end the watch. It also clears
                    // the queue, so anything held here is gone with it.
                    Console.Error.WriteLine($"  (cannot reach the server: {ex.Message})");
                    seen.Clear();
                    await Task.Delay(TimeSpan.FromSeconds(5), stopping.Token).ConfigureAwait(false);
                    continue;
                }

                // Anything decided elsewhere drops out of the queue; forget it so
                // a later request reusing nothing of it still prints.
                seen.IntersectWith(pending.Pending.Select(p => p.Id));

                foreach (var item in pending.Pending)
                {
                    if (!seen.Add(item.Id)) continue;

                    Console.WriteLine();
                    PrintApproval(item, showCommands: !interactive);

                    if (!interactive) continue;

                    if (await PromptAsync(client, item, approver, stopping.Token).ConfigureAwait(false))
                        seen.Remove(item.Id);
                }

                await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // ctrl-c. Anything still queued stays queued.
        }

        Console.WriteLine();
        Console.WriteLine("stopped watching. anything undecided is still waiting.");
        return 0;
    }

    /// <summary>
    /// Asks, and records the answer. Returns true when the request was decided,
    /// so the caller stops tracking it.
    ///
    /// Skipping is a first-class answer and the default for anything unrecognised:
    /// somebody who does not understand what they are looking at should be able to
    /// leave it for a person who does, and the safe direction is always "not yet".
    /// </summary>
    static async Task<bool> PromptAsync(
        RtfqClient client, PendingApprovalInfo item, string approver, CancellationToken cancellationToken)
    {
        Console.Write($"  approve {item.Id}? [a/d/s] ");
        var answer = Console.ReadLine()?.Trim().ToLowerInvariant();

        var approved = answer is "a" or "approve" or "y" or "yes";
        var denied = answer is "d" or "deny" or "n" or "no";

        if (!approved && !denied)
        {
            Console.WriteLine("  skipped; still waiting.");
            return false;
        }

        try
        {
            var result = await client
                .DecideApprovalAsync(item.Id, approved, approver, null, cancellationToken)
                .ConfigureAwait(false);

            Console.WriteLine($"  {result.Id}: {result.Outcome} by {approver}");
            return true;
        }
        catch (RtfqClientException ex)
        {
            // Most likely it lapsed or somebody else answered while this one was
            // being read. Say so rather than looking like the decision landed.
            Console.WriteLine($"  not recorded [{ex.Code}]: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// The statement and the rows, and nothing else. Per CLAUDE.md principle 3
    /// the approver never sees a natural-language summary, because the agent that
    /// wrote one may be the reason this needs approving.
    /// </summary>
    static void PrintApproval(PendingApprovalInfo item, bool showCommands = true)
    {
        Console.WriteLine(new string('-', 72));
        Console.WriteLine($"{item.Id}  {item.Kind} on {item.Source}/{item.Target}"
            + (item.AffectedRows is { } n ? $"  ({n} row(s))" : ""));
        Console.WriteLine($"requested by token '{item.TokenId}', expires {item.ExpiresAt}");
        Console.WriteLine();
        Console.WriteLine("  statement:");
        var newline = (char)10;
        foreach (var line in item.Statement.ReplaceLineEndings(newline.ToString()).Split(newline))
            Console.WriteLine("    " + line);

        if (item.DiffColumns.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  rows as they are now:");
            Console.WriteLine("    " + string.Join(" | ", item.DiffColumns));
            foreach (var row in ParseRows(item.DiffRows))
                Console.WriteLine("    " + row);
        }

        if (!showCommands) return;

        Console.WriteLine();
        Console.WriteLine($"  approve: rtfq approvals --approve {item.Id} --as YOU");
        Console.WriteLine($"  deny:    rtfq approvals --deny {item.Id} --as YOU");
    }

    static IEnumerable<string> ParseRows(string diffRows)
    {
        JsonNode? parsed;
        try { parsed = JsonNode.Parse(diffRows); }
        catch (System.Text.Json.JsonException) { yield break; }

        if (parsed is not JsonArray rows) yield break;

        foreach (var row in rows)
        {
            yield return row is JsonArray cells
                ? string.Join(" | ", cells.Select(c => c?.ToString() ?? "NULL"))
                : row?.ToString() ?? "";
        }
    }

    static async Task<int> UnlockAsync(Args args)
    {
        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var source = args.Value("source") ?? args.Positional.FirstOrDefault();
        if (string.IsNullOrEmpty(source)) return Fail("a source is required: rtfq unlock SOURCE --write --ttl 15m");

        var level = args.Has("schema") ? "schema" : "write";
        if (args.Has("write")) { /* the default; accepted so the documented form works */ }
        var ttl = args.Value("ttl") ?? "15m";
        if (Leftovers(args)) return 2;

        var result = await client.UnlockAsync(source, level, ttl).ConfigureAwait(false);
        if (result.Hint is { } hint) Console.WriteLine(hint);
        foreach (var u in result.Unlocked)
            Console.WriteLine($"  {u.Source}  {u.Level}  until {u.ExpiresAt}  (opened by {u.Who})");
        return 0;
    }

    static async Task<int> LockAsync(Args args)
    {
        using var client = BuildClient(args, out var error);
        if (client is null) return Fail(error!);

        var source = args.Value("source") ?? args.Positional.FirstOrDefault();
        if (string.IsNullOrEmpty(source)) return Fail("a source is required: rtfq lock SOURCE");
        if (Leftovers(args)) return 2;

        var result = await client.LockAsync(source).ConfigureAwait(false);
        Console.WriteLine(result.Hint ?? $"{source} is locked.");
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
              rtfq approvals [--approve ID | --deny ID] [--as NAME] [--reason TEXT]
              rtfq approvals --watch [--as NAME]      stay open; decide as they arrive
              rtfq unlock    SOURCE [--write | --schema] [--ttl 15m]
              rtfq lock      SOURCE
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
