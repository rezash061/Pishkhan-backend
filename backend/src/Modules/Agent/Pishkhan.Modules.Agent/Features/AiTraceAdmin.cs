using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Agent.Features;

/// <summary>
/// Filters of the cross-workspace trace list. <c>outcome</c>: an <c>AiReplyOutcomes</c> value; <c>maxConfidence</c> finds low-confidence answers;
/// <c>from</c> / <c>to</c> are ISO 8601 instants.
/// </summary>
public sealed record AdminTraceSearch(
    Guid? WorkspaceId, string? Outcome, string? Intent, string? HandoffReason, double? MaxConfidence, bool? GuardIssues, DateTimeOffset? From, DateTimeOffset? To,
    int? Page, int? PageSize);

/// <summary>A trace without customer content: no message, reply, tool arguments or results.</summary>
public sealed record AdminTraceSummary(
    Guid Id, Guid WorkspaceId, Guid ConversationId, string? Intent, string? Language, string Outcome, string? HandoffReason, double? Confidence, double Threshold,
    string? Prompts, string? Models, long InputTokens, long OutputTokens, decimal CostIrr, int LatencyMs, int ToolCallCount, int GuardIssueCount,
    DateTimeOffset CreatedAt);

/// <summary>A tool call. <c>arguments</c> and <c>result</c> are null until revealed.</summary>
public sealed record AdminTraceToolCall(string Name, string? Arguments, string? Result, string? Error);

/// <summary>
/// One trace. Customer content (<c>goal</c>, <c>input</c>, <c>reply</c>, tool arguments and results, guard details) is null while <c>redacted</c>;
/// <c>POST .../reveal</c> with a reason returns it and writes an audit entry the workspace owner sees.
/// </summary>
public sealed record AdminTraceDetail(
    AdminTraceSummary Trace, bool Redacted, Guid MessageId, Guid? FlowRunId, string? Goal, string? Input, string? Reply, string? Sentiment, double? ClassifierConfidence,
    double? SelfConfidence, IReadOnlyList<TraceFact> Facts, IReadOnlyList<AdminTraceToolCall> ToolCalls, IReadOnlyList<TraceGuardIssue> GuardIssues);

public sealed record TraceRevealRequest(string Reason);

/// <summary>Counts by outcome over a period, for the traces page header and the dashboard.</summary>
public sealed record AdminTraceStats(DateTimeOffset From, int Total, IReadOnlyDictionary<string, int> Outcomes, int LowConfidence, int WithGuardIssues, decimal CostIrr);

/// <summary>AdminApi: AI traces across workspaces (admin plan 4.5 traces viewer). Content is redacted unless a reason is given.</summary>
internal static class AiTraceAdminEndpoints
{
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.AiOps, StaffRoles.Support, StaffRoles.ReadOnly];
    private static readonly string[] Revealers = [StaffRoles.SuperAdmin, StaffRoles.AiOps, StaffRoles.Support];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().MapGroup("/ai/traces").WithTags("AI");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var reveal = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Revealers));

        read.MapGet(string.Empty, ListAsync)
            .WithSummary("AI traces across workspaces, newest first, without customer content")
            .WithDescription("Roles: SuperAdmin, AIOps, Support, ReadOnly.")
            .Produces<PagedResult<AdminTraceSummary>>()
            .WithStandardProblems();

        read.MapGet("/stats", StatsAsync)
            .WithSummary("Trace counts by outcome over the last `hours` (default 24, at most 720)")
            .Produces<AdminTraceStats>();

        read.MapGet("/{id:guid}", GetAsync)
            .WithSummary("One trace, redacted")
            .Produces<AdminTraceDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        reveal.MapPost("/{id:guid}/reveal", RevealAsync)
            .WithSummary("One trace with customer content")
            .WithDescription("Roles: SuperAdmin, AIOps, Support. Reason of 5 to 300 characters; audited as ai.trace.reveal in the workspace's audit log.")
            .Produces<AdminTraceDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();
    }

    private static IQueryable<AiTrace> All(AgentDbContext db) => db.Traces.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    /// <summary>Guard issues are a converted jsonb column, so the filter is SQL.</summary>
    private static IQueryable<AiTrace> WithGuardIssues(AgentDbContext db) =>
        db.Traces.FromSql($"SELECT * FROM agent.ai_traces WHERE jsonb_array_length(guard_issues) > 0").AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static async Task<IResult> ListAsync([AsParameters] AdminTraceSearch search, AgentDbContext db, CancellationToken ct)
    {
        var page = Math.Max(1, search.Page ?? 1);
        var size = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var query = search.GuardIssues == true ? WithGuardIssues(db) : All(db);
        if (search.WorkspaceId is { } ws)
        {
            query = query.Where(t => t.WorkspaceId == ws);
        }

        if (!string.IsNullOrWhiteSpace(search.Outcome))
        {
            query = query.Where(t => t.Outcome == search.Outcome.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search.Intent))
        {
            query = query.Where(t => t.Intent == search.Intent.Trim());
        }

        if (!string.IsNullOrWhiteSpace(search.HandoffReason))
        {
            query = query.Where(t => t.HandoffReason == search.HandoffReason.Trim());
        }

        if (search.MaxConfidence is { } max)
        {
            query = query.Where(t => t.Confidence != null && t.Confidence < max);
        }

        if (search.From is { } from)
        {
            query = query.Where(t => t.CreatedAt >= from);
        }

        if (search.To is { } to)
        {
            query = query.Where(t => t.CreatedAt < to);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<AdminTraceSummary>([.. rows.Select(Summary)], total, page, size));
    }

    private static async Task<IResult> StatsAsync(int? hours, AgentDbContext db, IClock clock, CancellationToken ct)
    {
        var from = clock.UtcNow.AddHours(-Math.Clamp(hours ?? 24, 1, 720));
        var period = All(db).Where(t => t.CreatedAt >= from);
        var outcomes = await period.GroupBy(t => t.Outcome).Select(g => new { g.Key, Count = g.Count(), Cost = g.Sum(t => t.CostIrr) }).ToListAsync(ct);
        var low = await period.CountAsync(t => t.Confidence != null && t.Confidence < t.Threshold, ct);
        var guarded = await WithGuardIssues(db).CountAsync(t => t.CreatedAt >= from, ct);
        return Results.Ok(new AdminTraceStats(
            from, outcomes.Sum(o => o.Count), outcomes.ToDictionary(o => o.Key, o => o.Count), low, guarded, outcomes.Sum(o => o.Cost)));
    }

    private static async Task<IResult> GetAsync(Guid id, AgentDbContext db, CancellationToken ct)
    {
        var trace = await All(db).FirstOrDefaultAsync(t => t.Id == id, ct);
        return trace is null ? Error.NotFound().ToProblem() : Results.Ok(Detail(trace, reveal: false));
    }

    private static async Task<IResult> RevealAsync(
        Guid id, TraceRevealRequest request, AgentDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var length = request.Reason?.Trim().Length ?? 0;
        if (length is < 5 or > 300)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["reason"] = [length < 5 ? "validation.too_short" : "validation.too_long"] }).ToProblem();
        }

        var trace = await All(db).FirstOrDefaultAsync(t => t.Id == id, ct);
        if (trace is null)
        {
            return Error.NotFound().ToProblem();
        }

        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), "ai.trace.reveal", "ai_trace", id.ToString(), trace.WorkspaceId,
            clock.UtcNow, reason: request.Reason!.Trim());
        db.Publish(entry, entry.PartitionKey(), trace.WorkspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(Detail(trace, reveal: true));
    }

    private static AdminTraceSummary Summary(AiTrace t) => new(
        t.Id, t.WorkspaceId, t.ConversationId, t.Intent, t.Language, t.Outcome, t.HandoffReason, t.Confidence, t.Threshold, t.Prompts, t.Models, t.InputTokens,
        t.OutputTokens, t.CostIrr, t.LatencyMs, t.ToolCalls.Count, t.GuardIssues.Count, t.CreatedAt);

    internal static AdminTraceDetail Detail(AiTrace t, bool reveal) => new(
        Summary(t),
        !reveal,
        t.MessageId,
        t.FlowRunId,
        reveal ? t.Goal : null,
        reveal ? t.Input : null,
        reveal ? t.Reply : null,
        t.Sentiment,
        t.ClassifierConfidence,
        t.SelfConfidence,
        t.Facts,
        [.. t.ToolCalls.Select(c => new AdminTraceToolCall(c.Name, reveal ? c.Arguments : null, reveal ? c.Result : null, c.Error))],
        [.. t.GuardIssues.Select(g => reveal ? g : g with { Detail = string.Empty })]);
}
