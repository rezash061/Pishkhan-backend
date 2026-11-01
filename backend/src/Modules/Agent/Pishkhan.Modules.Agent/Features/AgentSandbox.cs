using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Agent.Replies;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Agent.Features;

/// <summary>A message of the test conversation so far. <c>role</c>: customer or agent.</summary>
public sealed record SandboxTurn(string Role, string Text);

/// <summary>
/// One customer message in test mode. <c>history</c> is the conversation before it (the panel keeps it, at most 20 messages);
/// <c>tags</c> pretend the customer has these contact tags (to try the VIP rule); <c>goal</c> pretends a flow handed over.
/// </summary>
public sealed record SandboxRequest(string Text, IReadOnlyList<SandboxTurn>? History, IReadOnlyList<string>? Tags, string? Goal);

/// <summary>
/// What the agent would do. <c>messages</c> is what the customer would receive (the reply, or the handoff message). For a handoff,
/// <c>suggestedReply</c> is what the person would see. <c>trace</c> is not stored.
/// </summary>
public sealed record SandboxResponse(
    string Outcome, IReadOnlyList<string> Messages, string? HandoffReason, string? SuggestedReply, string? UnansweredQuestion, TraceDetail Trace);

/// <summary>
/// Test mode (backend plan 10.3): the owner chats with the agent through the same pipeline, with the trace in the answer. Nothing is
/// sent to Instagram, no trace or unanswered question is stored, and it does not count toward the plan's AI meters; it is limited
/// to <see cref="PerMinute"/> messages per workspace instead.
/// </summary>
internal static class AgentSandbox
{
    public const int PerMinute = 20;
    public const int MaxHistory = 20;
    public const int MaxText = 1_000;

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/agent").WithTags("Agent")
            .MapPost("/sandbox", SendAsync)
            .RequireWorkspacePermission(Permissions.AgentWrite)
            .WithSummary("Test mode: what the AI agent would answer, with its trace (nothing is sent or stored)")
            .Produces<SandboxResponse>()
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithStandardProblems();
    }

    private static async Task<IResult> SendAsync(
        Guid workspaceId, SandboxRequest request, AgentDbContext db, ReplyPipeline pipeline, RedisSlidingWindow window, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > MaxText)
        {
            errors["text"] = ["validation.agent.sandbox_text"];
        }

        var history = request.History ?? [];
        if (history.Count > MaxHistory
            || history.Any(t => t is null || t.Role is not ("customer" or "agent") || string.IsNullOrWhiteSpace(t.Text) || t.Text.Length > MaxText))
        {
            errors["history"] = ["validation.agent.sandbox_history"];
        }

        if (request.Goal is { Length: > 500 })
        {
            errors["goal"] = ["validation.agent.sandbox_goal"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (!(await window.TryHitAsync($"agent-sandbox:{workspaceId:N}", PerMinute, TimeSpan.FromMinutes(1))).Allowed)
        {
            return Error.RateLimited("agent.sandbox.rate_limited").ToProblem();
        }

        var now = clock.UtcNow;
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(ct) ?? AgentProfile.Default(workspaceId);
        var conversation = new ConversationSnapshot(
            Guid.Empty, workspaceId, Guid.Empty, Guid.Empty, "sandbox", ConversationStatuses.Open, false, now, now.AddDays(1), "مشتری آزمایشی", null,
            [.. (request.Tags ?? []).Where(t => !string.IsNullOrWhiteSpace(t)).Take(20)], new Dictionary<string, string>(), ChannelKinds.Sandbox);
        var before = history.Select((t, i) => new MessageSnapshot(
            Guid.Empty, now.AddSeconds(i - history.Count), t.Role == "customer" ? MessageDirections.Inbound : MessageDirections.Outbound, "text", t.Text,
            t.Role == "agent" ? HandledBy.Ai : null, MessageStatuses.Sent)).ToList();

        var decision = await pipeline.RunAsync(new ReplyInput(conversation, Guid.Empty, request.Text.Trim(), before, request.Goal, null, null), profile, ct);
        var messages = decision.Outcome == AiReplyOutcomes.Sent
            ? [decision.Reply!]
            : string.IsNullOrWhiteSpace(profile.HandoffMessage) ? Array.Empty<string>() : [profile.HandoffMessage];
        return Results.Ok(new SandboxResponse(
            decision.Outcome, messages, decision.HandoffReason, decision.SuggestedReply, decision.UnansweredQuestion, AgentEndpoints.ToDetail(decision.Trace)));
    }
}
