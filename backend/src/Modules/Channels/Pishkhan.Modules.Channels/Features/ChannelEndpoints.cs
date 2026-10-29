using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;
using StackExchange.Redis;

namespace Pishkhan.Modules.Channels.Features;

public sealed record ChannelResponse(Guid Id, string Kind, string ExternalId, string? Username, string? DisplayName, string Status, string? StatusReason, DateTimeOffset? TokenExpiresAt, DateTimeOffset CreatedAt);

public sealed record MediaResponse(string Id, string? Caption, string MediaType, Uri? MediaUrl, Uri? ThumbnailUrl, Uri? Permalink, DateTimeOffset? Timestamp);

public sealed record ConnectInstagramResponse(Uri AuthorizeUrl);

/// <summary>OAuth state: encrypted and authenticated with <see cref="ISecretProtector"/>, single use through a Redis nonce, valid 10 minutes.</summary>
internal sealed record OAuthState(Guid WorkspaceId, Guid UserId, string Nonce, DateTimeOffset ExpiresAt);

internal static class ChannelEndpoints
{
    private const string StatePurpose = "channels.oauth-state";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/channels").WithTags("Channels");

        ws.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Connected channel accounts")
            .Produces<IReadOnlyList<ChannelResponse>>();

        ws.MapPost("/instagram/connect", ConnectAsync)
            .RequireWorkspacePermission(Permissions.ChannelsManage)
            .WithSummary("Start connecting an Instagram professional account")
            .WithDescription("Returns the Instagram authorization URL. The browser is sent back to the panel after the callback with `status=connected|denied|error` and `reason`.")
            .Produces<ConnectInstagramResponse>()
            .WithStandardProblems();

        ws.MapGet("/{channelId:guid}/media", ListMediaAsync)
            .RequireWorkspacePermission(Permissions.AutomationWrite)
            .WithSummary("Recent posts and reels of an Instagram account, newest first")
            .WithDescription("For post-specific rules. `cursor` comes from the previous page; `limit` is 1 to 50 (default 25). Read live from Instagram.")
            .Produces<Page<MediaResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        ws.MapDelete("/{channelId:guid}", DisconnectAsync)
            .RequireWorkspacePermission(Permissions.ChannelsManage)
            .WithSummary("Disconnect a channel account; its token is erased")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        app.MapPanelAnonymousApi().MapGet("/channels/instagram/callback", CallbackAsync)
            .WithTags("Channels")
            .ExcludeFromDescription();
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, ChannelsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.Accounts.AsNoTracking()
            .Where(a => a.Status != ChannelStatuses.Disconnected)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new ChannelResponse(a.Id, a.Kind, a.ExternalId, a.Username, a.DisplayName, a.Status, a.StatusReason, a.TokenExpiresAt, a.CreatedAt))
            .ToListAsync(ct));

    private static async Task<IResult> ConnectAsync(
        Guid workspaceId,
        ICurrentUser caller,
        ISecretProtector secrets,
        InstagramGraphClient instagram,
        IOptions<InstagramOptions> options,
        [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
        RedisKeys keys,
        IClock clock)
    {
        if (string.IsNullOrEmpty(options.Value.AppId) || string.IsNullOrEmpty(options.Value.RedirectUri))
        {
            return Error.Unprocessable("channels.instagram.not_configured").ToProblem();
        }

        var nonce = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var state = new OAuthState(workspaceId, caller.UserId, nonce, clock.UtcNow.Add(StateLifetime));
        await redis.GetDatabase().StringSetAsync(keys.For("ig-oauth", nonce), workspaceId.ToString(), StateLifetime);
        var protectedState = secrets.Protect(JsonSerializer.Serialize(state, EnvelopeJson.Options), StatePurpose);
        return Results.Ok(new ConnectInstagramResponse(instagram.BuildAuthorizeUrl(protectedState)));
    }

    private static async Task<IResult> CallbackAsync(
        string? code,
        string? state,
        string? error,
        HttpContext http,
        ChannelsDbContext db,
        ITenantContext tenant,
        ISecretProtector secrets,
        IWorkspaceAccessResolver access,
        InstagramGraphClient instagram,
        IOptions<InstagramOptions> options,
        [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
        RedisKeys keys,
        IClock clock,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(ChannelEndpoints));
        var returnUrl = options.Value.PanelReturnUrl;
        IResult Back(string status, string? reason = null, Guid? channelId = null) => Results.Redirect(
            $"{returnUrl}?status={status}" + (reason is null ? string.Empty : $"&reason={Uri.EscapeDataString(reason)}") + (channelId is null ? string.Empty : $"&channel={channelId}"));

        if (!string.IsNullOrEmpty(error))
        {
            return Back("denied");
        }

        var parsed = ReadState(state, secrets);
        if (parsed is null || parsed.ExpiresAt < clock.UtcNow || string.IsNullOrEmpty(code)
            || !await redis.GetDatabase().KeyDeleteAsync(keys.For("ig-oauth", parsed.Nonce)))
        {
            return Back("error", "invalid_state");
        }

        var membership = await access.ResolveAsync(parsed.UserId, parsed.WorkspaceId, http.RequestAborted);
        if (membership is null || membership.Suspended || !membership.Permissions.Contains(Permissions.ChannelsManage))
        {
            return Back("error", "forbidden");
        }

        tenant.SetWorkspace(parsed.WorkspaceId);
        var ct = http.RequestAborted;
        try
        {
            var shortLived = await instagram.ExchangeCodeAsync(code, ct);
            var longLived = await instagram.ExchangeForLongLivedAsync(shortLived.AccessToken, ct);
            var profile = await instagram.GetMeAsync(longLived.AccessToken, ct);
            await instagram.SubscribeAppAsync(longLived.AccessToken, ct);

            var elsewhere = await db.Accounts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter])
                .AnyAsync(a => a.Kind == ChannelKinds.Instagram && a.ExternalId == profile.UserId && a.Status != ChannelStatuses.Disconnected && a.WorkspaceId != parsed.WorkspaceId, ct);
            if (elsewhere)
            {
                return Back("error", "already_connected_elsewhere");
            }

            var now = clock.UtcNow;
            var tokenEnc = secrets.Protect(longLived.AccessToken, TokenPurposes.Instagram);
            var expiresAt = now.AddSeconds(longLived.ExpiresInSeconds);
            var account = await db.Accounts
                .Where(a => a.Kind == ChannelKinds.Instagram && a.ExternalId == profile.UserId)
                .OrderBy(a => a.Status == ChannelStatuses.Disconnected)
                .FirstOrDefaultAsync(ct);
            if (account is null)
            {
                account = ChannelAccount.ConnectInstagram(parsed.WorkspaceId, profile.UserId, profile.Username, profile.Name, tokenEnc, expiresAt, shortLived.Permissions, parsed.UserId, now);
                db.Accounts.Add(account);
                db.PublishStatus(account);
            }
            else if (account.Reconnect(profile.Username, profile.Name, tokenEnc, expiresAt, shortLived.Permissions, now))
            {
                db.PublishStatus(account);
            }
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Instagram account {ExternalId} connected to workspace {WorkspaceId}", profile.UserId, parsed.WorkspaceId);
            return Back("connected", channelId: account.Id);
        }
        catch (InstagramApiException ex)
        {
            logger.LogWarning("Instagram OAuth failed: {ErrorCode} ({Code}/{Subcode})", ex.ErrorCode, ex.Code, ex.Subcode);
            return Back("error", ex.ErrorCode);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Instagram unreachable during OAuth");
            return Back("error", "instagram.unreachable");
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Back("error", "already_connected_elsewhere");
        }
    }

    private static async Task<IResult> ListMediaAsync(Guid workspaceId, Guid channelId, string? cursor, int? limit, ChannelsDbContext db, InstagramAccountGateway gateway, InstagramGraphClient instagram, CancellationToken ct)
    {
        var account = await db.Accounts.AsNoTracking().FirstOrDefaultAsync(a => a.Id == channelId && a.Status != ChannelStatuses.Disconnected, ct);
        if (account is null)
        {
            return Error.NotFound().ToProblem();
        }

        var result = await gateway.CallAsync(account.Id, RateFamilies.Read, token => instagram.ListMediaAsync(token, cursor, limit ?? 25, ct), ct);
        if (result.Value is not { } page)
        {
            return result.ErrorCode switch
            {
                "channel.unavailable" or "instagram.token_invalid" => Error.Conflict("channels.reauth_required").ToProblem(),
                _ => Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, type: ProblemTypes.Unavailable, title: result.ErrorCode),
            };
        }

        return Results.Ok(new Page<MediaResponse>(
            [.. page.Items.Select(m => new MediaResponse(m.Id, m.Caption, m.MediaType, ChannelProfileReader.ToUri(m.MediaUrl), ChannelProfileReader.ToUri(m.ThumbnailUrl), ChannelProfileReader.ToUri(m.Permalink), m.Timestamp))],
            page.NextCursor));
    }

    private static async Task<IResult> DisconnectAsync(Guid workspaceId, Guid channelId, ChannelsDbContext db, ISecretProtector secrets, InstagramGraphClient instagram, IClock clock, ILoggerFactory loggers, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == channelId && a.Status != ChannelStatuses.Disconnected, ct);
        if (account is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (account is { Kind: ChannelKinds.Instagram, TokenEnc: { } enc })
        {
            try
            {
                await instagram.UnsubscribeAppAsync(secrets.Unprotect(enc, TokenPurposes.Instagram), ct);
            }
            catch (Exception ex) when (ex is InstagramApiException or HttpRequestException)
            {
                // Best effort: the token may already be revoked. Webhooks for a disconnected account are dropped by ingest anyway.
                loggers.CreateLogger(typeof(ChannelEndpoints)).LogInformation("Unsubscribe on disconnect failed for {ChannelAccountId}: {Message}", account.Id, ex.Message);
            }
        }

        if (account.Disconnect("disconnected_by_user", clock.UtcNow))
        {
            db.PublishStatus(account);
        }

        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static OAuthState? ReadState(string? state, ISecretProtector secrets)
    {
        if (string.IsNullOrEmpty(state))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<OAuthState>(secrets.Unprotect(state, StatePurpose), EnvelopeJson.Options);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or JsonException)
        {
            return null;
        }
    }
}
