using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Agent.Features;

public sealed record HandoffRulesDto(bool HumanRequest, bool Complaint, bool DiscountRequest, bool RepeatedLowConfidence, IReadOnlyList<string> Keywords, IReadOnlyList<string> VipTags);

/// <summary>
/// The AI agent's settings. <c>tone</c>: friendly, formal or playful. <c>confidenceThreshold</c>: 0.3 to 0.95; below it the agent hands off
/// with its reply as a suggestion. <c>handoffMessage</c>: sent to the customer on a handoff (null sends nothing).
/// </summary>
public sealed record AgentProfileDto(
    string Name,
    string Tone,
    string? BrandVoice,
    bool ReplyInCustomerLanguage,
    IReadOnlyList<string> NeverSay,
    double ConfidenceThreshold,
    HandoffRulesDto Handoff,
    string? HandoffMessage,
    IReadOnlyList<string> AllowedDomains);

public sealed record AgentProfileResponse(AgentProfileDto Profile, bool IsDefault, DateTimeOffset? UpdatedAt);

public sealed record TraceSummary(
    Guid Id, Guid ConversationId, Guid MessageId, string Input, string? Intent, string Outcome, string? HandoffReason, string? Reply, double? Confidence,
    double Threshold, int LatencyMs, DateTimeOffset CreatedAt);

public sealed record TraceDetail(
    Guid Id, Guid ConversationId, Guid MessageId, Guid? FlowRunId, string? Goal, string Input, string? Intent, string? Language, string? Sentiment,
    double? ClassifierConfidence, IReadOnlyList<TraceFact> Facts, IReadOnlyList<TraceToolCall> ToolCalls, IReadOnlyList<TraceGuardIssue> GuardIssues,
    string? Reply, double? SelfConfidence, double? Confidence, double Threshold, string Outcome, string? HandoffReason, string? Prompts, string? Models,
    long InputTokens, long OutputTokens, decimal CostIrr, int LatencyMs, DateTimeOffset CreatedAt);

internal static class AgentEndpoints
{
    public const double MinThreshold = 0.3;
    public const double MaxThreshold = 0.95;

    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/agent").WithTags("Agent");

        group.MapGet("/profile", GetProfileAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The AI agent's persona, guardrails and handoff rules (defaults until saved)")
            .Produces<AgentProfileResponse>();

        group.MapPut("/profile", PutProfileAsync)
            .RequireWorkspacePermission(Permissions.AgentWrite)
            .WithSummary("Save the AI agent's settings")
            .Produces<AgentProfileResponse>()
            .WithStandardProblems();

        group.MapGet("/traces", ListTracesAsync)
            .RequireWorkspacePermission(Permissions.AgentWrite)
            .WithSummary("What the agent did for each customer turn, newest first; filter by conversation or outcome")
            .Produces<Page<TraceSummary>>();

        group.MapGet("/traces/{traceId:guid}", GetTraceAsync)
            .RequireWorkspacePermission(Permissions.AgentWrite)
            .WithSummary("One trace: classification, retrieved facts, tool calls, guard issues, confidence and usage")
            .Produces<TraceDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static AgentProfileDto ToDto(AgentProfile p) => new(
        p.Name, p.Tone, p.BrandVoice, p.ReplyInCustomerLanguage, p.NeverSay, p.ConfidenceThreshold,
        new HandoffRulesDto(p.Handoff.HumanRequest, p.Handoff.Complaint, p.Handoff.DiscountRequest, p.Handoff.RepeatedLowConfidence, p.Handoff.Keywords, p.Handoff.VipTags),
        p.HandoffMessage, p.AllowedDomains);

    private static async Task<IResult> GetProfileAsync(Guid workspaceId, AgentDbContext db, CancellationToken ct)
    {
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(ct);
        return Results.Ok(new AgentProfileResponse(ToDto(profile ?? AgentProfile.Default(workspaceId)), profile is null, profile?.UpdatedAt));
    }

    private static async Task<IResult> PutProfileAsync(Guid workspaceId, AgentProfileDto request, AgentDbContext db, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > AgentProfile.MaxName)
        {
            errors["name"] = ["validation.agent.name"];
        }

        if (request.Tone is null || !AgentTones.All.Contains(request.Tone))
        {
            errors["tone"] = ["validation.agent.tone"];
        }

        if (request.BrandVoice is { Length: > AgentProfile.MaxBrandVoice })
        {
            errors["brandVoice"] = ["validation.agent.brand_voice"];
        }

        if (request.ConfidenceThreshold is < MinThreshold or > MaxThreshold || double.IsNaN(request.ConfidenceThreshold))
        {
            errors["confidenceThreshold"] = ["validation.agent.threshold"];
        }

        if (request.HandoffMessage is { Length: > AgentProfile.MaxHandoffMessage })
        {
            errors["handoffMessage"] = ["validation.agent.handoff_message"];
        }

        var neverSay = Phrases(request.NeverSay, "neverSay", errors);
        var keywords = Phrases(request.Handoff?.Keywords, "handoff.keywords", errors);
        var vipTags = Phrases(request.Handoff?.VipTags, "handoff.vipTags", errors);
        var domains = new List<string>();
        foreach (var raw in request.AllowedDomains ?? [])
        {
            var value = raw?.Trim().ToLowerInvariant() ?? string.Empty;
            value = value.StartsWith("http", StringComparison.Ordinal) && Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : value.TrimEnd('/');
            if (value.StartsWith("www.", StringComparison.Ordinal))
            {
                value = value[4..];
            }

            if (Uri.CheckHostName(value) != UriHostNameType.Dns || !value.Contains('.', StringComparison.Ordinal))
            {
                errors["allowedDomains"] = ["validation.agent.domain"];
                break;
            }

            if (!domains.Contains(value, StringComparer.Ordinal))
            {
                domains.Add(value);
            }
        }

        if (domains.Count > AgentProfile.MaxList)
        {
            errors["allowedDomains"] = ["validation.agent.too_many"];
        }

        if (request.Handoff is null)
        {
            errors["handoff"] = ["validation.required"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var profile = await db.Profiles.FirstOrDefaultAsync(ct);
        if (profile is null)
        {
            profile = AgentProfile.Default(workspaceId);
            profile.CreatedAt = clock.UtcNow;
            db.Profiles.Add(profile);
        }

        profile.Name = request.Name.Trim();
        profile.Tone = request.Tone!;
        profile.BrandVoice = string.IsNullOrWhiteSpace(request.BrandVoice) ? null : request.BrandVoice.Trim();
        profile.ReplyInCustomerLanguage = request.ReplyInCustomerLanguage;
        profile.NeverSay = neverSay;
        profile.ConfidenceThreshold = Math.Round(request.ConfidenceThreshold, 2);
        profile.Handoff = new HandoffRules
        {
            HumanRequest = request.Handoff!.HumanRequest,
            Complaint = request.Handoff.Complaint,
            DiscountRequest = request.Handoff.DiscountRequest,
            RepeatedLowConfidence = request.Handoff.RepeatedLowConfidence,
            Keywords = keywords,
            VipTags = vipTags,
        };
        profile.HandoffMessage = string.IsNullOrWhiteSpace(request.HandoffMessage) ? null : request.HandoffMessage.Trim();
        profile.AllowedDomains = domains;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new AgentProfileResponse(ToDto(profile), false, profile.UpdatedAt));
    }

    private static List<string> Phrases(IReadOnlyList<string>? values, string field, Dictionary<string, string[]> errors)
    {
        var result = new List<string>();
        foreach (var raw in values ?? [])
        {
            var value = raw?.Trim() ?? string.Empty;
            if (value.Length == 0 || value.Length > AgentProfile.MaxPhrase)
            {
                errors[field] = ["validation.agent.phrase"];
                return result;
            }

            if (!result.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(value);
            }
        }

        if (result.Count > AgentProfile.MaxList)
        {
            errors[field] = ["validation.agent.too_many"];
        }

        return result;
    }

    private static async Task<IResult> ListTracesAsync(
        Guid workspaceId, Guid? conversationId, string? outcome, string? cursor, int? limit, AgentDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 50, 1, 100);
        var query = db.Traces.AsNoTracking();
        if (conversationId is { } c)
        {
            query = query.Where(t => t.ConversationId == c);
        }

        if (!string.IsNullOrEmpty(outcome))
        {
            if (!AiReplyOutcomes.All.Contains(outcome))
            {
                return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["outcome"] = ["validation.agent.outcome"] }).ToProblem();
            }

            query = query.Where(t => t.Outcome == outcome);
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(t => EF.Functions.LessThan(ValueTuple.Create(t.CreatedAt, t.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Take(take + 1)
            .Select(t => new TraceSummary(t.Id, t.ConversationId, t.MessageId, t.Input, t.Intent, t.Outcome, t.HandoffReason, t.Reply, t.Confidence, t.Threshold, t.LatencyMs, t.CreatedAt))
            .ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<TraceSummary>([.. rows.Take(take)], next));
    }

    private static async Task<IResult> GetTraceAsync(Guid workspaceId, Guid traceId, AgentDbContext db, CancellationToken ct)
    {
        var t = await db.Traces.AsNoTracking().FirstOrDefaultAsync(x => x.Id == traceId, ct);
        return t is null ? Error.NotFound().ToProblem() : Results.Ok(ToDetail(t));
    }

    public static TraceDetail ToDetail(AiTrace t) => new(
        t.Id, t.ConversationId, t.MessageId, t.FlowRunId, t.Goal, t.Input, t.Intent, t.Language, t.Sentiment, t.ClassifierConfidence, t.Facts, t.ToolCalls,
        t.GuardIssues, t.Reply, t.SelfConfidence, t.Confidence, t.Threshold, t.Outcome, t.HandoffReason, t.Prompts, t.Models, t.InputTokens, t.OutputTokens,
        t.CostIrr, t.LatencyMs, t.CreatedAt);
}
