using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Domain;
using Pishkhan.Modules.Automation.Engine;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Automation.Features;

/// <summary>A rule. Higher <c>priority</c> wins when several rules match; <c>channelAccountId</c> null applies to every account.</summary>
public sealed record RuleRequest(string Name, Guid? ChannelAccountId, string TriggerKind, TriggerConfig? Trigger, IReadOnlyList<RuleAction>? Actions, int Priority, bool Enabled = true);

/// <summary><c>postId</c> is the post a comment rule is bound to (for <c>next_post</c>, set once the next post gets its first comment).</summary>
public sealed record RuleResponse(
    Guid Id, string Name, Guid? ChannelAccountId, string TriggerKind, TriggerConfig Trigger, IReadOnlyList<RuleAction> Actions, string? PostId,
    int Priority, bool Enabled, int Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record RuleStatsDayResponse(DateOnly Day, int Fired, int PrivateReplies, int PublicReplies, int FlowsStarted);

/// <summary>Totals over the period, per day, comments still waiting for their paced reply, and the latest backlog run.</summary>
public sealed record RuleStatsResponse(
    Guid RuleId, int Days, int Fired, int PrivateReplies, int PublicReplies, int FlowsStarted, int CommentsQueued,
    IReadOnlyList<RuleStatsDayResponse> PerDay, BacklogRunResponse? LatestBacklogRun);

public sealed record BacklogRunResponse(Guid Id, string Status, string MediaId, int Pages, int CommentsSeen, int CommentsQueued, string? Error, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

public sealed record FlowRequest(string Name, FlowGraph? Graph);

public sealed record FlowResponse(Guid Id, string Name, string Status, FlowGraph Graph, int? PublishedVersion, DateTimeOffset? PublishedAt, FlowValidation Validation, DateTimeOffset UpdatedAt);

public sealed record SettingsRequest(bool AiEnabled, string? FallbackText, int? FallbackCooldownMinutes);

/// <summary>Business hours are the workspace's (<c>GET/PUT /workspaces/{workspaceId}/business-hours</c>).</summary>
public sealed record SettingsResponse(bool AiEnabled, string? FallbackText, int FallbackCooldownMinutes);

internal static class AutomationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var root = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/automation").WithTags("Automation");

        root.MapGet("/rules", ListRulesAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Rules, highest priority first")
            .Produces<IReadOnlyList<RuleResponse>>();

        root.MapPost("/rules", CreateRuleAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Create a rule")
            .WithDescription("Triggers: dm_keyword, comment_keyword, comment_any, story_reply, story_mention, live_comment, post_share, new_conversation, postback, outside_business_hours. Keyword matching ignores Arabic/Persian letter variants, digits, spacing and half-spaces.")
            .Produces<RuleResponse>(StatusCodes.Status201Created)
            .WithStandardProblems();

        root.MapGet("/rules/{ruleId:guid}", GetRuleAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .Produces<RuleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/rules/{ruleId:guid}", UpdateRuleAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .Produces<RuleResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapDelete("/rules/{ruleId:guid}", DeleteRuleAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPost("/rules/{ruleId:guid}/backlog-run", StartBacklogAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Reply to the post's existing comments")
            .WithDescription("Pages through the comments of the rule's post and answers those within Meta's 7-day private reply limit, paced like live comments. Comments already answered are skipped. One run per rule at a time (409).")
            .Produces<BacklogRunResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        root.MapGet("/rules/{ruleId:guid}/stats", RuleStatsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("How often the rule fired and replied, per day (UTC), for the last `days` (default 30)")
            .Produces<RuleStatsResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapGet("/flows", ListFlowsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .Produces<IReadOnlyList<FlowResponse>>();

        root.MapPost("/flows", CreateFlowAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Create a flow (draft)")
            .Produces<FlowResponse>(StatusCodes.Status201Created)
            .WithStandardProblems();

        root.MapGet("/flows/{flowId:guid}", GetFlowAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .Produces<FlowResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPut("/flows/{flowId:guid}", UpdateFlowAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Edit the draft; published versions do not change until the next publish")
            .Produces<FlowResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        root.MapDelete("/flows/{flowId:guid}", DeleteFlowAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPost("/flows/{flowId:guid}/validate", ValidateFlowAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Validate the draft without publishing")
            .Produces<FlowValidation>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        root.MapPost("/flows/{flowId:guid}/publish", PublishFlowAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Publish the draft as a new version")
            .WithDescription("Runs already in progress keep the version they started on. An invalid draft is refused with 422 and the validation result.")
            .Produces<FlowResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        root.MapGet("/settings", GetSettingsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .Produces<SettingsResponse>();

        root.MapPut("/settings", PutSettingsAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("AI on or off, fallback message")
            .WithDescription("The outside-business-hours trigger and flow conditions use the workspace's business hours (`/workspaces/{workspaceId}/business-hours`).")
            .Produces<SettingsResponse>()
            .WithStandardProblems();

        AutomationSandbox.Map(root);
    }

    private static RuleResponse ToResponse(AutomationRule r) =>
        new(r.Id, r.Name, r.ChannelAccountId, r.TriggerKind, r.Trigger, r.Actions, r.PostExternalId, r.Priority, r.Enabled, r.Revision, r.CreatedAt, r.UpdatedAt);

    private static FlowResponse ToResponse(Flow f) =>
        new(f.Id, f.Name, f.Status, f.Draft, f.PublishedVersion, f.PublishedAt, FlowValidator.Validate(f.Draft), f.UpdatedAt);

    private static BacklogRunResponse ToResponse(BacklogRun b) =>
        new(b.Id, b.Status, b.MediaId, b.Pages, b.CommentsSeen, b.CommentsQueued, b.Error, b.CreatedAt, b.CompletedAt);

    private static async Task<IResult> ListRulesAsync(Guid workspaceId, Guid? channelAccountId, AutomationDbContext db, CancellationToken ct)
    {
        var query = db.Rules.AsNoTracking();
        if (channelAccountId is { } account)
        {
            query = query.Where(r => r.ChannelAccountId == account || r.ChannelAccountId == null);
        }

        var rules = await query.OrderByDescending(r => r.Priority).ThenBy(r => r.CreatedAt).Take(500).ToListAsync(ct);
        return Results.Ok(rules.Select(ToResponse).ToList());
    }

    private static async Task<IResult> GetRuleAsync(Guid workspaceId, Guid ruleId, AutomationDbContext db, CancellationToken ct) =>
        await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, ct) is { } rule ? Results.Ok(ToResponse(rule)) : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateRuleAsync(
        Guid workspaceId, RuleRequest request, AutomationDbContext db, IChannelDirectory channels, ICatalogQueries catalog, ICacheInvalidator invalidator, AutomationCache cache, IClock clock, CancellationToken ct)
    {
        var definition = Definition(request);
        if (await ValidateAsync(definition, db, channels, catalog, ct) is { } invalid)
        {
            return invalid;
        }

        var rule = AutomationRule.Create(workspaceId, definition, clock.UtcNow);
        db.Rules.Add(rule);
        await SaveRuleChangeAsync(db, rule, invalidator, cache, ct);
        return Results.Json(ToResponse(rule), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateRuleAsync(
        Guid workspaceId, Guid ruleId, RuleRequest request, AutomationDbContext db, IChannelDirectory channels, ICatalogQueries catalog, ICacheInvalidator invalidator, AutomationCache cache, CancellationToken ct)
    {
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule is null)
        {
            return Error.NotFound().ToProblem();
        }

        var definition = Definition(request);
        if (await ValidateAsync(definition, db, channels, catalog, ct) is { } invalid)
        {
            return invalid;
        }

        rule.Update(definition);
        await SaveRuleChangeAsync(db, rule, invalidator, cache, ct);
        return Results.Ok(ToResponse(rule));
    }

    private static async Task<IResult> DeleteRuleAsync(Guid workspaceId, Guid ruleId, AutomationDbContext db, ICacheInvalidator invalidator, AutomationCache cache, IClock clock, CancellationToken ct)
    {
        var rule = await db.Rules.FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule is null)
        {
            return Error.NotFound().ToProblem();
        }

        rule.Delete(clock.UtcNow);
        await SaveRuleChangeAsync(db, rule, invalidator, cache, ct);
        return Results.NoContent();
    }

    private static RuleDefinition Definition(RuleRequest r) =>
        new(r.Name ?? string.Empty, r.ChannelAccountId, r.TriggerKind ?? string.Empty, r.Trigger ?? new TriggerConfig(), r.Actions ?? [], r.Priority, r.Enabled);

    private static async Task<IResult?> ValidateAsync(RuleDefinition definition, AutomationDbContext db, IChannelDirectory channels, ICatalogQueries catalog, CancellationToken ct)
    {
        var errors = RuleValidator.Validate(definition).ToList();
        if (definition.ChannelAccountId is { } account && await channels.GetAsync(account, ct) is null)
        {
            errors.Add(("channelAccountId", "automation.channel_not_found"));
        }

        foreach (var flowId in definition.Actions.Where(a => a.Kind == ActionKinds.StartFlow && a.FlowId is not null).Select(a => a.FlowId!.Value))
        {
            if (!await db.Flows.AnyAsync(f => f.Id == flowId && f.PublishedVersion != null, ct))
            {
                errors.Add(("actions", "automation.flow_not_published"));
            }
        }

        foreach (var cardSetId in definition.Actions.Where(a => a.Kind == ActionKinds.SendCards && a.CardSetId is not null).Select(a => a.CardSetId!.Value))
        {
            if (await catalog.GetCardSetAsync(cardSetId, ct) is null)
            {
                errors.Add(("actions", "automation.card_set_not_found"));
            }
        }

        return errors.Count == 0 ? null : Invalid(errors);
    }

    private static async Task SaveRuleChangeAsync(AutomationDbContext db, AutomationRule rule, ICacheInvalidator invalidator, AutomationCache cache, CancellationToken ct)
    {
        db.Publish(new RulesChanged(rule.WorkspaceId, rule.Id, rule.Revision), rule.WorkspaceId.ToString());
        invalidator.Enqueue(db, AutomationCacheTags.Workspace(rule.WorkspaceId));
        await db.SaveChangesAsync(ct);
        await cache.InvalidateLocalAsync(rule.WorkspaceId, ct);
    }

    private static async Task<IResult> StartBacklogAsync(Guid workspaceId, Guid ruleId, AutomationDbContext db, IClock clock, CancellationToken ct)
    {
        var rule = await db.Rules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId, ct);
        if (rule is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (!TriggerKinds.Comment.Contains(rule.TriggerKind) || rule.TriggerKind == TriggerKinds.LiveComment || rule.PostExternalId is null || rule.ChannelAccountId is null)
        {
            return Error.Unprocessable("automation.backlog_needs_post").ToProblem();
        }

        if (!rule.Enabled)
        {
            return Error.Unprocessable("automation.rule_disabled").ToProblem();
        }

        var run = BacklogRun.Start(workspaceId, rule.Id, rule.ChannelAccountId.Value, rule.PostExternalId, clock.UtcNow);
        db.BacklogRuns.Add(run);
        db.Publish(new BacklogPageRequested(run.Id, null), run.Id.ToString());
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("automation.backlog_running").ToProblem();
        }

        return Results.Json(ToResponse(run), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> RuleStatsAsync(Guid workspaceId, Guid ruleId, int? days, AutomationDbContext db, IClock clock, CancellationToken ct)
    {
        if (!await db.Rules.AnyAsync(r => r.Id == ruleId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var period = Math.Clamp(days ?? 30, 1, 365);
        var now = clock.UtcNow;
        var from = DateOnly.FromDateTime(now.UtcDateTime).AddDays(1 - period);
        var rows = await db.RuleStats.AsNoTracking().Where(s => s.RuleId == ruleId && s.Day >= from).OrderBy(s => s.Day).ToListAsync(ct);
        var queued = await db.CommentEvents.CountAsync(e => e.RuleId == ruleId && e.Outcome == CommentOutcomes.Queued && (e.PrivateReplyDueAt > now || e.PublicReplyDueAt > now), ct);
        var backlog = await db.BacklogRuns.AsNoTracking().Where(b => b.RuleId == ruleId).OrderByDescending(b => b.CreatedAt).FirstOrDefaultAsync(ct);
        return Results.Ok(new RuleStatsResponse(
            ruleId, period, rows.Sum(r => r.Fired), rows.Sum(r => r.PrivateReplies), rows.Sum(r => r.PublicReplies), rows.Sum(r => r.FlowsStarted), queued,
            [.. rows.Select(r => new RuleStatsDayResponse(r.Day, r.Fired, r.PrivateReplies, r.PublicReplies, r.FlowsStarted))],
            backlog is null ? null : ToResponse(backlog)));
    }

    private static async Task<IResult> ListFlowsAsync(Guid workspaceId, AutomationDbContext db, CancellationToken ct) =>
        Results.Ok((await db.Flows.AsNoTracking().OrderBy(f => f.Name).Take(500).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> GetFlowAsync(Guid workspaceId, Guid flowId, AutomationDbContext db, CancellationToken ct) =>
        await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flowId, ct) is { } flow ? Results.Ok(ToResponse(flow)) : Error.NotFound().ToProblem();

    private static async Task<IResult> CreateFlowAsync(Guid workspaceId, FlowRequest request, AutomationDbContext db, IClock clock, CancellationToken ct)
    {
        if (ValidateFlowRequest(request) is { } invalid)
        {
            return invalid;
        }

        var flow = Flow.Create(workspaceId, request.Name, request.Graph!, clock.UtcNow);
        db.Flows.Add(flow);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(flow), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateFlowAsync(Guid workspaceId, Guid flowId, FlowRequest request, AutomationDbContext db, CancellationToken ct)
    {
        if (ValidateFlowRequest(request) is { } invalid)
        {
            return invalid;
        }

        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null)
        {
            return Error.NotFound().ToProblem();
        }

        flow.Edit(request.Name, request.Graph!);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(flow));
    }

    private static async Task<IResult> DeleteFlowAsync(Guid workspaceId, Guid flowId, AutomationDbContext db, IClock clock, CancellationToken ct)
    {
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null)
        {
            return Error.NotFound().ToProblem();
        }

        flow.DeletedAt = clock.UtcNow;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> ValidateFlowAsync(Guid workspaceId, Guid flowId, AutomationDbContext db, CancellationToken ct) =>
        await db.Flows.AsNoTracking().FirstOrDefaultAsync(f => f.Id == flowId, ct) is { } flow ? Results.Ok(FlowValidator.Validate(flow.Draft)) : Error.NotFound().ToProblem();

    private static async Task<IResult> PublishFlowAsync(Guid workspaceId, Guid flowId, AutomationDbContext db, IClock clock, CancellationToken ct)
    {
        var flow = await db.Flows.FirstOrDefaultAsync(f => f.Id == flowId, ct);
        if (flow is null)
        {
            return Error.NotFound().ToProblem();
        }

        var validation = FlowValidator.Validate(flow.Draft);
        if (!validation.IsValid)
        {
            return Results.Json(validation, statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        var version = flow.Publish(clock.UtcNow);
        db.FlowVersions.Add(version);
        db.Publish(new FlowPublished(workspaceId, flow.Id, version.Version), flow.Id.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(flow));
    }

    private static SettingsResponse ToResponse(AutomationSettings s) => new(s.AiEnabled, s.FallbackText, s.FallbackCooldownMinutes);

    private static async Task<IResult> GetSettingsAsync(Guid workspaceId, AutomationDbContext db, CancellationToken ct) =>
        Results.Ok(ToResponse(await db.Settings.AsNoTracking().FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct) ?? AutomationSettings.Default(workspaceId)));

    private static async Task<IResult> PutSettingsAsync(
        Guid workspaceId, SettingsRequest request, AutomationDbContext db, ICacheInvalidator invalidator, AutomationCache cache, CancellationToken ct)
    {
        var errors = new List<(string, string)>();
        if (request.FallbackText is { Length: > 1000 })
        {
            errors.Add(("fallbackText", "validation.text.length"));
        }

        if (request.FallbackCooldownMinutes is < 0 or > 30 * 24 * 60)
        {
            errors.Add(("fallbackCooldownMinutes", "automation.cooldown_invalid"));
        }

        if (errors.Count > 0)
        {
            return Invalid(errors);
        }

        var settings = await db.Settings.FirstOrDefaultAsync(s => s.WorkspaceId == workspaceId, ct);
        if (settings is null)
        {
            settings = AutomationSettings.Default(workspaceId);
            db.Settings.Add(settings);
        }

        settings.AiEnabled = request.AiEnabled;
        settings.FallbackText = string.IsNullOrWhiteSpace(request.FallbackText) ? null : request.FallbackText.Trim();
        settings.FallbackCooldownMinutes = request.FallbackCooldownMinutes ?? AutomationSettings.DefaultFallbackCooldownMinutes;
        invalidator.Enqueue(db, AutomationCacheTags.Workspace(workspaceId));
        await db.SaveChangesAsync(ct);
        await cache.InvalidateLocalAsync(workspaceId, ct);
        return Results.Ok(ToResponse(settings));
    }

    private static IResult? ValidateFlowRequest(FlowRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 100)
        {
            return Invalid([("name", "validation.name.length")]);
        }

        if (request.Graph is null || request.Graph.Nodes is null || request.Graph.Edges is null)
        {
            return Invalid([("graph", "flow.empty")]);
        }

        return request.Graph.Nodes.Count > FlowValidator.MaxNodes || request.Graph.Edges.Count > FlowValidator.MaxNodes * 4
            ? Invalid([("graph", "flow.too_many_nodes")])
            : null;
    }

    private static IResult Invalid(IReadOnlyList<(string Field, string Code)> errors) =>
        Error.Validation(errors[0].Code, errors.GroupBy(e => e.Field).ToDictionary(g => g.Key, g => g.Select(e => e.Code).Distinct().ToArray(), StringComparer.Ordinal)).ToProblem();
}
