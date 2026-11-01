using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Domain;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Agent.Replies;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;
using StackExchange.Redis;

namespace Pishkhan.Modules.Agent.Features;

/// <summary>The website's live demo (backend plan 9.16): the AI agent of a fixed demo shop, with its own knowledge and products.</summary>
public sealed class DemoChatOptions
{
    public const string Section = "DemoChat";

    /// <summary>The demo shop's workspace. Empty: the endpoint answers 404 <c>demo.unavailable</c>.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>Messages per chat session (the site shows <c>remaining</c>).</summary>
    public int MessagesPerSession { get; set; } = 10;

    /// <summary>Messages per visitor IP per hour, over all sessions.</summary>
    public int PerIpPerHour { get; set; } = 30;

    /// <summary>Messages per day for the whole site, a cost cap.</summary>
    public int PerDay { get; set; } = 3_000;

    /// <summary>How long a session's messages are kept for context.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(6);
}

/// <summary>
/// One visitor message. <c>sessionId</c> is made by the browser; <c>altcha</c> (a solved <c>GET /altcha/challenge</c>) is needed on the
/// first message of a session.
/// </summary>
public sealed record DemoChatRequest(Guid SessionId, string Message, string? Altcha);

/// <summary>
/// The demo agent's answer. <c>confidence</c> is 0..1; <c>sources</c> are the titles of the demo shop's knowledge it used;
/// <c>remaining</c> is how many messages this session has left. <c>handedOff</c>: in a real shop a person would take over here.
/// </summary>
public sealed record DemoChatResponse(string Reply, double Confidence, IReadOnlyList<string> Sources, int Remaining, bool HandedOff);

/// <summary>
/// <c>POST /api/v1/public/demo-chat</c>: the same reply pipeline as a real conversation, against the demo workspace. Nothing is stored
/// in the database; the session's last messages live in Redis for <see cref="DemoChatOptions.SessionLifetime"/>. Limits: messages per
/// session (429 <c>demo.limit</c>), per IP and per day (429 <c>demo.rate_limited</c>).
/// </summary>
internal static class DemoChat
{
    public const int MaxMessage = 500;
    public const int MaxSources = 3;

    /// <summary>Said when the agent would hand a real conversation to a person and the demo shop has no handoff message.</summary>
    public const string HandoffReply = "در فروشگاه واقعی، این گفتگو اینجا به یکی از همکاران شما سپرده می‌شود تا خودش جواب بدهد.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPublicApi().WithTags("Website")
            .MapPost("/demo-chat", SendAsync)
            .WithSummary("The website's live demo: one visitor message to the demo shop's AI agent")
            .WithDescription("ALTCHA is needed on the first message of a session (400 `validation.altcha.invalid`). After `remaining` reaches 0 the answer is 429 `demo.limit`; too many messages from one IP or in a day is 429 `demo.rate_limited`; 404 `demo.unavailable` when no demo shop is configured.")
            .Produces<DemoChatResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithStandardProblems();
    }

    private static async Task<IResult> SendAsync(
        DemoChatRequest request, IOptions<DemoChatOptions> options, AltchaVerifier altcha, RedisSlidingWindow window,
        [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, ITenantContext tenant, ICurrentUser caller,
        AgentDbContext db, ReplyPipeline pipeline, IClock clock, CancellationToken ct)
    {
        var o = options.Value;
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.SessionId == Guid.Empty)
        {
            errors["sessionId"] = ["validation.required"];
        }

        var text = request.Message?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > MaxMessage)
        {
            errors["message"] = [string.IsNullOrEmpty(text) ? "validation.required" : "validation.too_long"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (o.WorkspaceId is not { } workspaceId || workspaceId == Guid.Empty)
        {
            return Error.NotFound("demo.unavailable").ToProblem();
        }

        var store = redis.GetDatabase();
        var countKey = keys.For("demo-chat", $"{request.SessionId:N}:n");
        var historyKey = keys.For("demo-chat", $"{request.SessionId:N}:h");
        var used = (int)((long?)await store.StringGetAsync(countKey) ?? 0);
        if (used >= o.MessagesPerSession)
        {
            return Error.RateLimited("demo.limit").ToProblem();
        }

        if (used == 0 && !await altcha.VerifyAsync(request.Altcha))
        {
            return Error.Validation("validation.altcha.invalid", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["altcha"] = ["validation.altcha.invalid"] })
                .ToProblem();
        }

        if (!(await window.TryHitAsync($"demo-chat-ip:{caller.IpAddress ?? "unknown"}", o.PerIpPerHour, TimeSpan.FromHours(1))).Allowed
            || !(await window.TryHitAsync("demo-chat-all", o.PerDay, TimeSpan.FromDays(1))).Allowed)
        {
            return Error.RateLimited("demo.rate_limited").ToProblem();
        }

        // INCR settles two messages sent at once: only the ones within the cap go through.
        var count = (int)await store.StringIncrementAsync(countKey);
        await store.KeyExpireAsync(countKey, o.SessionLifetime);
        if (count > o.MessagesPerSession)
        {
            return Error.RateLimited("demo.limit").ToProblem();
        }

        var history = (await store.ListRangeAsync(historyKey))
            .Select(v => JsonSerializer.Deserialize<SandboxTurn>((string)v!, Json))
            .OfType<SandboxTurn>()
            .ToList();

        tenant.SetWorkspace(workspaceId);
        var now = clock.UtcNow;
        var profile = await db.Profiles.AsNoTracking().FirstOrDefaultAsync(ct) ?? AgentProfile.Default(workspaceId);
        var conversation = new ConversationSnapshot(
            Guid.Empty, workspaceId, Guid.Empty, Guid.Empty, "demo", ConversationStatuses.Open, false, now, now.AddDays(1), "بازدیدکننده سایت", null,
            [], new Dictionary<string, string>(), ChannelKinds.Sandbox);
        var before = history.Select((t, i) => new MessageSnapshot(
            Guid.Empty, now.AddSeconds(i - history.Count), t.Role == "customer" ? MessageDirections.Inbound : MessageDirections.Outbound, "text", t.Text,
            t.Role == "agent" ? HandledBy.Ai : null, MessageStatuses.Sent)).ToList();

        var decision = await pipeline.RunAsync(new ReplyInput(conversation, Guid.Empty, text!, before, null, null, null), profile, ct);
        var handedOff = decision.Outcome != AiReplyOutcomes.Sent;
        var reply = !handedOff ? decision.Reply! : string.IsNullOrWhiteSpace(profile.HandoffMessage) ? HandoffReply : profile.HandoffMessage;

        await store.ListRightPushAsync(historyKey, [
            JsonSerializer.Serialize(new SandboxTurn("customer", text!), Json),
            JsonSerializer.Serialize(new SandboxTurn("agent", reply), Json),
        ]);
        await store.ListTrimAsync(historyKey, -2 * o.MessagesPerSession, -1);
        await store.KeyExpireAsync(historyKey, o.SessionLifetime);

        var sources = decision.Trace.Facts.OrderByDescending(f => f.Score).Select(f => f.Title).Distinct(StringComparer.Ordinal).Take(MaxSources).ToList();
        return Results.Ok(new DemoChatResponse(
            reply, Math.Round(Math.Clamp(decision.Trace.Confidence ?? 0, 0, 1), 2), sources, Math.Max(0, o.MessagesPerSession - count), handedOff));
    }
}
