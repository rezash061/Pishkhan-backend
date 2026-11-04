using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Channels.Bale;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Channels.Features;

/// <summary>The bot's token from Bale's BotFather (بات‌فادر).</summary>
public sealed record ConnectBaleRequest(string BotToken);

/// <summary>Bale (BE-2.7): the seller connects their own bot; customers who start it talk to the shop in the same inbox.</summary>
internal static class BaleEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/channels").WithTags("Channels")
            .MapPost("/bale/connect", ConnectAsync)
            .RequireWorkspacePermission(Permissions.ChannelsManage)
            .WithSummary("Connect a Bale bot by its token")
            .WithDescription("Checks the token with Bale, registers the webhook and returns the account. 422 `channels.bale_token_invalid`, 409 `channels.bale_bot_taken` (connected to a workspace already), 402 `billing.plan_limit`, 503 `channels.bale_unavailable`.")
            .Produces<ChannelResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status402PaymentRequired)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        // Bale calls this; the signature in the path is checked before anything is read, so the URL cannot be forged or guessed.
        app.MapPanelAnonymousApi().MapPost("/channels/bale/webhook/{channelId:guid}/{signature}", WebhookAsync)
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ConnectAsync(
        Guid workspaceId, ConnectBaleRequest request, ChannelsDbContext db, BaleBotClient bale, ISecretProtector secrets, IEntitlements entitlements, ICurrentUser caller,
        IClock clock, CancellationToken ct)
    {
        var token = request.BotToken?.Trim() ?? string.Empty;
        if (token.Length is < 10 or > 200 || token.Any(char.IsWhiteSpace))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["botToken"] = ["channels.bale_token_invalid"] }).ToProblem();
        }

        var count = await db.Accounts.CountAsync(a => a.Status != ChannelStatuses.Disconnected, ct);
        if (!(await entitlements.GetAsync(workspaceId, ct)).Allows(BillingMeters.ChannelAccounts, count + 1))
        {
            return Error.PlanLimit(BillingMeters.ChannelAccounts).ToProblem();
        }

        BaleBot bot;
        try
        {
            bot = await bale.GetMeAsync(token, ct);
        }
        catch (BaleApiException e) when (e.Unauthorized)
        {
            return Error.Unprocessable("channels.bale_token_invalid").ToProblem();
        }
        catch (BaleApiException)
        {
            return Results.Problem(title: "channels.bale_unavailable", statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "channels.bale_unavailable" });
        }

        var botId = bot.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AnyAsync(a => a.Kind == ChannelKinds.Bale && a.ExternalId == botId && a.Status != ChannelStatuses.Disconnected, ct))
        {
            return Error.Conflict("channels.bale_bot_taken").ToProblem();
        }

        var now = clock.UtcNow;
        var account = ChannelAccount.ConnectBale(workspaceId, botId, bot.Username, bot.FirstName, secrets.Protect(token, TokenPurposes.Bale), caller.UserId, now);
        try
        {
            await bale.SetWebhookAsync(token, bale.WebhookUrl(account.Id), ct);
        }
        catch (BaleApiException)
        {
            return Results.Problem(title: "channels.bale_unavailable", statusCode: StatusCodes.Status503ServiceUnavailable,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "channels.bale_unavailable" });
        }

        db.Accounts.Add(account);
        db.PublishStatus(account, AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("channels.bale_bot_taken").ToProblem();
        }

        return Results.Json(
            new ChannelResponse(account.Id, account.Kind, account.ExternalId, account.Username, account.DisplayName, account.Status, account.StatusReason, null, account.CreatedAt),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> WebhookAsync(Guid channelId, string signature, JsonElement update, ChannelsDbContext db, BaleBotClient bale, CancellationToken ct)
    {
        if (!bale.IsValidSignature(channelId, signature))
        {
            return Results.NotFound();
        }

        var account = await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).AsNoTracking()
            .Where(a => a.Id == channelId && a.Kind == ChannelKinds.Bale && a.Status != ChannelStatuses.Disconnected)
            .Select(a => new { a.Id, a.WorkspaceId })
            .FirstOrDefaultAsync(ct);
        if (account is null)
        {
            // A disconnected bot: Bale stops retrying on 200.
            return Results.Ok();
        }

        db.Publish(new BaleUpdateReceived(account.Id, account.WorkspaceId, update.Clone()), account.Id.ToString(), workspaceId: account.WorkspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok();
    }
}
