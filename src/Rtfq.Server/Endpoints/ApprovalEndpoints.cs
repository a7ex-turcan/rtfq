using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rtfq.Contracts;
using Rtfq.Server.Approval;
using Rtfq.Server.Configuration;

namespace Rtfq.Server.Endpoints;

/// <summary>
/// The human side of the write path: what is waiting, and the answer.
///
/// Reaching these requires a token that has been granted write somewhere. That is
/// weaker than a separate approver identity, and it is stated plainly rather than
/// dressed up: with static tokens there is nothing better available, and pretending
/// otherwise would be worse than the gap. Real approver identity waits for the
/// identity work deferred past M5.
/// </summary>
internal static class ApprovalEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/v1/approvals", Pending);
        app.MapPost("/v1/approvals/decide", Decide);
        app.MapGet("/v1/unlocks", ListUnlocks);
        app.MapPost("/v1/unlocks", Unlock);
        app.MapPost("/v1/unlocks/lock", Lock);
    }

    // --- approvals -----------------------------------------------------------

    static async Task Pending(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "approvals");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;
        if (!await RequireApproverAsync(scope, caller).ConfigureAwait(false)) return;

        var provider = ctx.RequestServices.GetService<LocalApprovalProvider>();
        if (provider is null) { await NotOurQueue(ctx, scope).ConfigureAwait(false); return; }

        var pending = provider.Pending()
            .Where(p => caller.Grants.ContainsKey(p.Context.Source))
            .Select(p => new PendingApprovalInfo
            {
                Id = p.Id,
                Source = p.Context.Source,
                TokenId = p.Context.TokenId,
                Target = p.Context.Target,
                Kind = p.Context.Kind,
                Statement = p.Context.Statement,
                AffectedRows = p.Context.AffectedRows,
                DiffColumns = [.. p.Context.DiffColumns],
                DiffRows = p.Context.DiffRows,
                Fingerprint = p.Context.Fingerprint,
                RequestedAt = p.RequestedAt.ToString("O"),
                ExpiresAt = p.ExpiresAt.ToString("O"),
            })
            .ToList();

        await scope.Ok(new PendingApprovalsResponse(pending), RtfqApprovalJson.Default.PendingApprovalsResponse,
            classification: "approval").ConfigureAwait(false);
    }

    static async Task Decide(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "approval_decision");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;
        if (!await RequireApproverAsync(scope, caller).ConfigureAwait(false)) return;

        var body = await scope.ReadBodyAsync(RtfqApprovalJson.Default.ApprovalDecisionRequest).ConfigureAwait(false);
        if (body is null) return;

        if (string.IsNullOrWhiteSpace(body.Id) || string.IsNullOrWhiteSpace(body.Approver))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                "body must be {\"id\": \"...\", \"approved\": true|false, \"approver\": \"...\"}").ConfigureAwait(false);
            return;
        }

        var provider = ctx.RequestServices.GetService<LocalApprovalProvider>();
        if (provider is null) { await NotOurQueue(ctx, scope).ConfigureAwait(false); return; }

        if (!provider.Decide(body.Id, body.Approved, body.Approver, body.Reason))
        {
            await scope.Refuse(StatusCodes.Status404NotFound, ErrorCodes.SourceUnknown,
                "no such pending approval: it may have been decided already, or its handle expired").ConfigureAwait(false);
            return;
        }

        // Recorded here as well as at commit, so a decision is in the journal even
        // if the agent never comes back to collect it.
        scope.Audit(null, null, "approval", body.Approved ? "approved" : "denied", null, null, null);

        await scope.Ok(new ApprovalDecisionResponse(body.Id, body.Approved ? "approved" : "denied"),
            RtfqApprovalJson.Default.ApprovalDecisionResponse).ConfigureAwait(false);
    }

    /// <summary>
    /// Under a webhook provider the queue lives somewhere else entirely, and
    /// saying so is better than serving an empty list that reads as "nothing is
    /// waiting".
    /// </summary>
    static Task NotOurQueue(HttpContext ctx, RequestScope scope) =>
        scope.Refuse(StatusCodes.Status409Conflict, ErrorCodes.RequestMalformed,
            "this server sends approvals to the " +
            ctx.RequestServices.GetRequiredService<IApprovalProvider>().Name +
            " provider, so the queue is not here. Answer them where they are delivered.");

    // --- unlock ------------------------------------------------------------------

    static async Task ListUnlocks(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "unlocks");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var store = ctx.RequestServices.GetRequiredService<Policy.UnlockStore>();
        await WriteUnlocks(scope, store, null).ConfigureAwait(false);
    }

    static async Task Unlock(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "unlock");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var body = await scope.ReadBodyAsync(RtfqApprovalJson.Default.UnlockRequest).ConfigureAwait(false);
        if (body is null) return;

        if (!AccessLevels.TryParse(body.Level, out var level) || level == AccessLevel.Read)
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                "level must be write or schema; reads are never locked").ConfigureAwait(false);
            return;
        }

        // Unlocking a source needs the access it is unlocking. Opening a door you
        // could not walk through would be a strange privilege to hand out.
        if (!await scope.AuthoriseAsync(caller, body.Source, level).ConfigureAwait(false)) return;

        if (!Duration.TryParse(body.Ttl ?? "15m", out var ttl))
        {
            await scope.Refuse(StatusCodes.Status400BadRequest, ErrorCodes.RequestMalformed,
                $"'{body.Ttl}' is not a duration - use forms like 15m or 1h").ConfigureAwait(false);
            return;
        }

        var store = ctx.RequestServices.GetRequiredService<Policy.UnlockStore>();
        var state = store.Unlock(body.Source, level, ttl, caller.TokenId);

        scope.Audit(body.Source, null, "unlock", "unlocked", null, null, null);

        await WriteUnlocks(scope, store,
            $"{body.Source} is open to {level.ToWire()} until {state.ExpiresAt:HH:mm:ss}Z. " +
            "A restart re-locks it.").ConfigureAwait(false);
    }

    static async Task Lock(HttpContext ctx)
    {
        var scope = RequestScope.Begin(ctx, "lock");
        var caller = await scope.AuthenticateAsync().ConfigureAwait(false);
        if (caller is null) return;

        var body = await scope.ReadBodyAsync(RtfqApprovalJson.Default.UnlockRequest).ConfigureAwait(false);
        if (body is null) return;
        if (!await scope.AuthoriseAsync(caller, body.Source, AccessLevel.Read).ConfigureAwait(false)) return;

        var store = ctx.RequestServices.GetRequiredService<Policy.UnlockStore>();
        store.Lock(body.Source);

        scope.Audit(body.Source, null, "unlock", "locked", null, null, null);
        await WriteUnlocks(scope, store, $"{body.Source} is locked.").ConfigureAwait(false);
    }

    static Task WriteUnlocks(RequestScope scope, Policy.UnlockStore store, string? hint) =>
        scope.Ok(new UnlockResponse(
            [.. store.Current().Select(s => new UnlockInfo
            {
                Source = s.Source,
                Level = s.Level.ToWire(),
                Who = s.Who,
                ExpiresAt = s.ExpiresAt.ToString("O"),
            })],
            hint), RtfqApprovalJson.Default.UnlockResponse);

    /// <summary>
    /// Only a token that can write somewhere may see or answer approvals. A
    /// read-only agent has no business in the queue that exists to police it.
    /// </summary>
    static async Task<bool> RequireApproverAsync(RequestScope scope, Policy.Caller caller)
    {
        if (caller.Grants.Values.Any(level => level >= AccessLevel.Write)) return true;

        await scope.Refuse(StatusCodes.Status403Forbidden, ErrorCodes.InsufficientAccess,
            "approving changes requires a token with write access to some source").ConfigureAwait(false);
        return false;
    }
}
