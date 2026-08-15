using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rtfq.Adapters;
using Rtfq.Contracts;
using Rtfq.Server.Configuration;
using Rtfq.Server.Policy;
using Rtfq.Server.Schema;

namespace Rtfq.Server.Endpoints;

/// <summary>
/// The HTTP+JSON surface.
///
/// Written as explicit <see cref="HttpContext"/> delegates with hand-called
/// serialization rather than parameter-inferred minimal APIs: inference relies on
/// generated or reflected binding, and per ADR 0001 the only version of this code
/// that counts is the one that survives trimming.
/// </summary>
internal static class ApiEndpoints
{
    /// <summary>
    /// How many tables <c>describe_source</c> lists before it stops and tells the
    /// caller to filter. The ceiling exists because this output is paid for in
    /// context on every call; a thousand-table dump is not discovery.
    /// </summary>
    const int DefaultTableLimit = 80;
    const int MaxTableLimit = 500;

    public static void Map(WebApplication app)
    {
        app.MapGet("/health", async ctx =>
        {
            var scope = RequestScope.Begin(ctx, "health");
            await scope.Ok(new HealthResponse("ok", RtfqVersion.Current), RtfqJson.Default.HealthResponse)
                .ConfigureAwait(false);
        });

        app.MapGet("/v1/sources", ListSources);
        app.MapGet("/v1/sources/{source}", DescribeSource);
        app.MapGet("/v1/sources/{source}/tables/{table}", DescribeTable);
        app.MapPost("/v1/sources/{source}/refresh", Refresh);
        app.MapPost("/v1/query", Query);
        app.MapPost("/v1/sample", Sample);
        app.MapPost("/v1/explain", Explain);
    }

    // --- discovery ----------------------------------------------------------

    static async Task ListSources(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "list_sources");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var policy = ctx.RequestServices.GetRequiredService<PolicyEngine>();
        var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();

        var list = new List<SourceInfo>();
        foreach (var (source, effective) in policy.VisibleSources(caller))
        {
            list.Add(new SourceInfo
            {
                Name = source.Name,
                Kind = source.Kind,
                Description = source.Description,
                Access = source.Access.ToWire(),
                EffectiveAccess = effective.ToWire(),
                Capabilities = registry.TryGet(source.Name, out var adapter) ? adapter.Capabilities.ToWire() : [],
            });
        }

        await scope.Ok(new SourcesResponse(list), RtfqJson.Default.SourcesResponse).ConfigureAwait(false);
    }

    static async Task DescribeSource(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "describe_source");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var name = scope.Route("source") ?? "";
        if (!await scope.AuthoriseAsync(caller, name, AccessLevel.Read).ConfigureAwait(false)) return;

        var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
        var cache = ctx.RequestServices.GetRequiredService<SchemaCache>();
        var source = config.FindSource(name)!;

        CachedSchema cached;
        try
        {
            cached = await cache.GetAsync(name, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, name).ConfigureAwait(false);
            return;
        }

        var pattern = scope.Query("pattern");
        var limit = Math.Min(scope.QueryInt("limit") ?? DefaultTableLimit, MaxTableLimit);

        var matching = cached.Snapshot.Tables
            .Where(t => pattern is null ||
                        t.QualifiedName.Contains(pattern, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var shown = matching.Take(limit).ToList();
        var truncated = matching.Count > shown.Count;

        var effective = AccessLevels.Intersect(source.Access, caller.Grants[name]);

        await scope.Ok(new DescribeSourceResponse
        {
            Source = name,
            Kind = source.Kind,
            Description = source.Description,
            EffectiveAccess = effective.ToWire(),
            Schema = Freshness(cached),
            TableCount = matching.Count,
            Tables = [.. shown.Select(t => new TableSummary
            {
                Name = t.QualifiedName,
                Kind = t.Kind,
                EstimatedRows = t.EstimatedRows,
                Columns = t.Columns.Count,
            })],
            Truncated = truncated,
            Hint = truncated
                ? $"showing {shown.Count} of {matching.Count} tables; narrow with ?pattern= or raise ?limit= (max {MaxTableLimit})"
                : null,
        }, RtfqJson.Default.DescribeSourceResponse, name).ConfigureAwait(false);
    }

    static async Task DescribeTable(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "describe_table");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var name = scope.Route("source") ?? "";
        if (!await scope.AuthoriseAsync(caller, name, AccessLevel.Read).ConfigureAwait(false)) return;

        var tableName = Uri.UnescapeDataString(scope.Route("table") ?? "");
        var cache = ctx.RequestServices.GetRequiredService<SchemaCache>();

        CachedSchema cached;
        try
        {
            cached = await cache.GetAsync(name, ctx.RequestAborted).ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, name).ConfigureAwait(false);
            return;
        }

        var table = cached.Snapshot.Find(tableName);
        if (table is null)
        {
            await scope.Refuse(StatusCodes.Status404NotFound, ErrorCodes.SourceUnknown,
                $"no table '{tableName}' in '{name}' as of {Math.Round(cached.Age.TotalSeconds)}s ago; " +
                "call describe_source to list tables, or refresh if the schema just changed", name)
                .ConfigureAwait(false);
            return;
        }

        await scope.Ok(new DescribeTableResponse
        {
            Table = table.QualifiedName,
            Kind = table.Kind,
            EstimatedRows = table.EstimatedRows,
            // Writes arrive in M3. Reporting a hopeful true here would have an
            // agent draft statements it cannot run.
            Writable = false,
            Schema = Freshness(cached),
            Columns = [.. table.Columns.Select(c => new ColumnDetail(c.Name, c.Type, c.Nullable, c.Default))],
            PrimaryKey = table.PrimaryKey,
            Indexes = [.. table.Indexes.Select(i => new IndexDetail(i.Name, i.Columns, i.Unique, i.Primary))],
            ForeignKeys = [.. table.ForeignKeys.Select(f =>
                new ForeignKeyDetail(f.Columns, f.ReferencedTable, f.ReferencedColumns))],
        }, RtfqJson.Default.DescribeTableResponse, name).ConfigureAwait(false);
    }

    static async Task Refresh(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "refresh");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var name = scope.Route("source") ?? "";
        if (!await scope.AuthoriseAsync(caller, name, AccessLevel.Read).ConfigureAwait(false)) return;

        var cache = ctx.RequestServices.GetRequiredService<SchemaCache>();
        var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
        var source = config.FindSource(name)!;

        try
        {
            var refreshed = await cache.RefreshAsync(name, ctx.RequestAborted).ConfigureAwait(false);
            await scope.Ok(new DescribeSourceResponse
            {
                Source = name,
                Kind = source.Kind,
                Description = source.Description,
                EffectiveAccess = AccessLevels.Intersect(source.Access, caller.Grants[name]).ToWire(),
                Schema = Freshness(refreshed),
                TableCount = refreshed.Snapshot.Tables.Count,
                Tables = [],
                Truncated = false,
                Hint = "schema re-read; call describe_source to list tables",
            }, RtfqJson.Default.DescribeSourceResponse, name).ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, name).ConfigureAwait(false);
        }
    }

    // --- reads ----------------------------------------------------------------

    static async Task Query(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "query");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var body = await scope.ReadBodyAsync(RtfqJson.Default.QueryRequest).ConfigureAwait(false);
        if (body is null) return;

        if (string.IsNullOrWhiteSpace(body.Source))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                "body must be {\"source\": \"...\", \"statement\": \"...\"}").ConfigureAwait(false);
            return;
        }
        if (string.IsNullOrWhiteSpace(body.Statement))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.StatementEmpty,
                "statement is empty", body.Source).ConfigureAwait(false);
            return;
        }
        if (!await scope.AuthoriseAsync(caller, body.Source, AccessLevel.Read).ConfigureAwait(false)) return;

        var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
        var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();
        var source = config.FindSource(body.Source)!;
        var maxRows = ClampRows(body.MaxRows, source.EffectiveMaxRows(config.Defaults));

        try
        {
            var result = await registry[body.Source].ExecuteReadAsync(
                body.Statement,
                new ReadOptions(maxRows, source.EffectiveStatementTimeout(config.Defaults)),
                ctx.RequestAborted).ConfigureAwait(false);

            await scope.Ok(new QueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                RowCount = result.RowCount,
                Truncated = result.Truncated,
                ElapsedMs = scope.ElapsedMs,
                NextCursor = null,
                // ADR 0003: truncation is terminal, so the response has to say what
                // to do instead. "Ask again" is not available and never will be.
                Hint = result.Truncated
                    ? $"stopped at the {maxRows}-row cap; narrow the WHERE clause, aggregate, " +
                      "or ask for fewer columns - there is no pagination"
                    : null,
            }, RtfqJson.Default.QueryResponse, body.Source, body.Statement,
               rowCount: result.RowCount, truncated: result.Truncated).ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, body.Source, body.Statement).ConfigureAwait(false);
        }
    }

    static async Task Sample(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "sample");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var body = await scope.ReadBodyAsync(RtfqJson.Default.SampleRequest).ConfigureAwait(false);
        if (body is null) return;

        if (string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Table))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                "body must be {\"source\": \"...\", \"table\": \"...\"}").ConfigureAwait(false);
            return;
        }
        if (!await scope.AuthoriseAsync(caller, body.Source, AccessLevel.Read).ConfigureAwait(false)) return;

        var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
        var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();
        var source = config.FindSource(body.Source)!;

        // Sampling is for learning shape, so it is capped far below a query's
        // ceiling however much the caller asks for.
        var rows = Math.Clamp(body.Rows ?? 10, 1, Math.Min(100, source.EffectiveMaxRows(config.Defaults)));

        try
        {
            var result = await registry[body.Source].SampleAsync(body.Table, rows, ctx.RequestAborted)
                .ConfigureAwait(false);

            await scope.Ok(new QueryResponse
            {
                Columns = result.Columns,
                Rows = result.Rows,
                RowCount = result.RowCount,
                Truncated = result.Truncated,
                ElapsedMs = scope.ElapsedMs,
                Hint = result.Truncated ? $"sample capped at {rows} rows" : null,
            }, RtfqJson.Default.QueryResponse, body.Source, $"sample {body.Table}",
               rowCount: result.RowCount, truncated: result.Truncated).ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, body.Source, $"sample {body.Table}").ConfigureAwait(false);
        }
    }

    static async Task Explain(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "explain");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var body = await scope.ReadBodyAsync(RtfqJson.Default.ExplainRequest).ConfigureAwait(false);
        if (body is null) return;

        if (string.IsNullOrWhiteSpace(body.Source) || string.IsNullOrWhiteSpace(body.Statement))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                "body must be {\"source\": \"...\", \"statement\": \"...\"}").ConfigureAwait(false);
            return;
        }
        if (!await scope.AuthoriseAsync(caller, body.Source, AccessLevel.Read).ConfigureAwait(false)) return;

        var config = ctx.RequestServices.GetRequiredService<RtfqConfig>();
        var registry = ctx.RequestServices.GetRequiredService<SourceRegistry>();
        var source = config.FindSource(body.Source)!;

        try
        {
            var plan = await registry[body.Source].ExplainAsync(
                body.Statement,
                source.EffectiveStatementTimeout(config.Defaults),
                ctx.RequestAborted).ConfigureAwait(false);

            await scope.Ok(new ExplainResponse { Plan = plan, ElapsedMs = scope.ElapsedMs },
                RtfqJson.Default.ExplainResponse, body.Source, body.Statement, classification: "explain")
                .ConfigureAwait(false);
        }
        catch (AdapterException ex)
        {
            await scope.RefuseAdapter(ex, body.Source, body.Statement).ConfigureAwait(false);
        }
    }

    // --- helpers -----------------------------------------------------------------

    /// <summary>A caller may lower its own ceiling. It can never raise it.</summary>
    static int ClampRows(int? requested, int configured) =>
        requested is { } r && r > 0 ? Math.Min(r, configured) : configured;

    static SchemaFreshness Freshness(CachedSchema cached) => new()
    {
        CapturedAt = cached.Snapshot.CapturedAt.ToString("O"),
        AgeSeconds = (long)cached.Age.TotalSeconds,
        Stale = cached.Stale,
    };
}
