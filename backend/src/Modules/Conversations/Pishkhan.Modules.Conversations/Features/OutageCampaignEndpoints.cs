using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Conversations.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Conversations.Features;

/// <summary>
/// Who a campaign would reach now: contacts with a mobile and a granted SMS consent. <c>outage</c> is whether a campaign may be sent
/// (Instagram is down, or one of the workspace's Instagram accounts cannot send). <c>nextAllowedAt</c> is set during the 24-hour cooldown.
/// </summary>
public sealed record CampaignAudienceResponse(int Contacts, int WithPhone, int Reachable, bool Outage, DateTimeOffset? NextAllowedAt);

/// <summary>
/// The text (5 to 300 characters) every recipient gets; the shop's name is put in front. <c>{link}</c> becomes each recipient's own link to the
/// shop's Bale bot, so their Bale chat joins their contact (needs a connected Bale bot).
/// </summary>
public sealed record CampaignRequest(string Text);

/// <summary><c>status</c>: sending, completed or limit_reached (the plan's SMS allowance ran out).</summary>
/// <summary>A one-time link to the shop's Bale bot that joins the contact's Bale chat to them, valid 14 days.</summary>
public sealed record BaleLinkResponse(string Url, DateTimeOffset ExpiresAt);

public sealed record CampaignResponse(
    Guid Id, string Channel, string Text, string Status, int Scanned, int Sent, int Failed, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>"Move my customers" during an Instagram outage (BE-2.7, gap G11): one SMS to the contacts who agreed to receive one.</summary>
internal static class OutageCampaignEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/outage").WithTags("Outage");

        group.MapGet("/audience", AudienceAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("How many contacts a campaign would reach, and whether one may be sent now")
            .Produces<CampaignAudienceResponse>();

        group.MapPost("/campaigns", StartAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithIdempotency()
            .WithSummary("Text every contact who agreed to SMS, inviting them to another channel")
            .Produces<CampaignResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/campaigns", ListAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("Campaigns, newest first")
            .Produces<IReadOnlyList<CampaignResponse>>();

        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/contacts").WithTags("Outage")
            .MapPost("/{contactId:guid}/bale-link", BaleLinkAsync)
            .RequireWorkspacePermission(Permissions.CrmWrite)
            .WithSummary("A one-time link to the shop's Bale bot for this contact (send it on Instagram or by SMS)")
            .Produces<BaleLinkResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        group.MapGet("/campaigns/{campaignId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.CrmRead)
            .WithSummary("One campaign's progress")
            .Produces<CampaignResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static CampaignResponse ToResponse(OutageCampaign c) =>
        new(c.Id, c.Channel, c.Text, c.Status, c.Scanned, c.Sent, c.Failed, c.CreatedAt, c.CompletedAt);

    private static async Task<IResult> AudienceAsync(
        Guid workspaceId, ConversationsDbContext db, IChannelHealth health, IChannelDirectory channels, IClock clock, CancellationToken ct)
    {
        var counts = await db.Database.SqlQuery<AudienceRow>($"""
            SELECT count(*)::int AS "Contacts",
                   count(*) FILTER (WHERE c.fields ? 'phone')::int AS "WithPhone",
                   count(*) FILTER (WHERE c.fields ? 'phone' AND (
                       SELECT k.granted FROM conversations.contact_consents k
                       WHERE k.contact_id = c.id AND k.channel = {ConsentChannels.Sms} ORDER BY k.created_at DESC, k.id DESC LIMIT 1) IS TRUE)::int AS "Reachable"
            FROM conversations.contacts c
            WHERE c.workspace_id = {workspaceId}
            """).SingleAsync(ct);
        return Results.Ok(new CampaignAudienceResponse(
            counts.Contacts, counts.WithPhone, counts.Reachable, await OutageAsync(health, channels, ct), await NextAllowedAsync(db, clock, ct)));
    }

    private static async Task<IResult> StartAsync(
        Guid workspaceId, CampaignRequest request, ConversationsDbContext db, IChannelHealth health, IChannelDirectory channels, IWorkspaceQueries workspaces,
        ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var text = request.Text?.Trim() ?? string.Empty;
        if (text.Length is < 5 or > OutageCampaign.MaxText)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["text"] = ["validation.text.length"] }).ToProblem();
        }

        // An outage tool, not bulk marketing: only while Instagram (or the workspace's account) cannot reach customers.
        if (!await OutageAsync(health, channels, ct))
        {
            return Error.Unprocessable("outage.not_down").ToProblem();
        }

        if (await NextAllowedAsync(db, clock, ct) is not null)
        {
            return Error.Conflict("outage.campaign_cooldown").ToProblem();
        }

        if (text.Contains(OutageCampaign.LinkPlaceholder, StringComparison.Ordinal) && await BaleBotAsync(channels, ct) is null)
        {
            return Error.Unprocessable("outage.link_needs_bale").ToProblem();
        }

        var now = clock.UtcNow;
        var shop = await workspaces.GetNameAsync(workspaceId, ct) ?? string.Empty;
        var campaign = OutageCampaign.Start(workspaceId, $"{shop}:\n{text}", caller.UserId, now);
        db.OutageCampaigns.Add(campaign);
        db.Publish(new OutageCampaignBatchRequested(workspaceId, campaign.Id), campaign.Id.ToString(), workspaceId: workspaceId);
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "outage.campaign_started", "outage_campaign", campaign.Id.ToString(), workspaceId, now,
            after: new { campaign.Channel, length = campaign.Text.Length });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(campaign), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, ConversationsDbContext db, CancellationToken ct) =>
        Results.Ok((await db.OutageCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).Take(50).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid campaignId, ConversationsDbContext db, CancellationToken ct) =>
        await db.OutageCampaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId, ct) is { } campaign ? Results.Ok(ToResponse(campaign)) : Error.NotFound().ToProblem();

    private static async Task<IResult> BaleLinkAsync(
        Guid workspaceId, Guid contactId, ConversationsDbContext db, IChannelDirectory channels, IBaleLinks links, IClock clock, CancellationToken ct)
    {
        if (!await db.Contacts.AnyAsync(c => c.Id == contactId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        if (await BaleBotAsync(channels, ct) is not { } bot)
        {
            return Error.Unprocessable("channels.bale_not_connected").ToProblem();
        }

        var token = ContactLinkToken.Create(workspaceId, contactId, bot.Id, clock.UtcNow);
        db.ContactLinkTokens.Add(token);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new BaleLinkResponse(links.DeepLink(bot.Username!, token.Token), token.ExpiresAt));
    }

    /// <summary>The workspace's active Bale bot with a username (deep links need it), or null.</summary>
    internal static async Task<ChannelAccountInfo?> BaleBotAsync(IChannelDirectory channels, CancellationToken ct) =>
        (await channels.ListAsync(ct)).FirstOrDefault(a => a.Kind == ChannelKinds.Bale && a.Status == ChannelStatuses.Active && !string.IsNullOrEmpty(a.Username));

    /// <summary>Instagram is down, or one of the workspace's Instagram accounts can no longer send.</summary>
    private static async Task<bool> OutageAsync(IChannelHealth health, IChannelDirectory channels, CancellationToken ct) =>
        (await health.GetInstagramAsync(ct)).IsDown
        || (await channels.ListAsync(ct)).Any(a => a.Kind == ChannelKinds.Instagram && a.Status is ChannelStatuses.ReauthRequired or ChannelStatuses.Disconnected);

    private static async Task<DateTimeOffset?> NextAllowedAsync(ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        var last = await db.OutageCampaigns.AsNoTracking().OrderByDescending(c => c.CreatedAt).Select(c => (DateTimeOffset?)c.CreatedAt).FirstOrDefaultAsync(ct);
        return last is { } at && at.AddHours(OutageCampaign.CooldownHours) > clock.UtcNow ? at.AddHours(OutageCampaign.CooldownHours) : null;
    }

}

/// <summary>The audience counts, read with SQL (the consent subquery and the jsonb key test are not LINQ).</summary>
internal sealed class AudienceRow
{
    public int Contacts { get; set; }

    public int WithPhone { get; set; }

    public int Reachable { get; set; }
}
