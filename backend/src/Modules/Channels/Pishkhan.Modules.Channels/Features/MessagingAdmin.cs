using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Channels.Bale;
using Pishkhan.Modules.Channels.Instagram;
using StackExchange.Redis;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Channels.Features;

/// <summary>Sent and failed SMS in the last 24 hours, and by purpose.</summary>
public sealed record SmsTraffic(int Sent, int Failed, IReadOnlyDictionary<string, int> ByPurpose);

/// <summary>
/// The SMS provider (from configuration: provider, whether its API key is set, the OTP template, the remaining credit in Rial) and its
/// traffic. Credentials are deployment secrets and are never returned or edited here.
/// </summary>
public sealed record SmsOverview(SmsProviderStatus Provider, SmsTraffic Last24Hours);

/// <summary>
/// Platform Bale settings. <c>webhookSecretSet</c> false means the development default is in use (must be set in production). <c>bots</c>
/// counts sellers' bots by status.
/// </summary>
public sealed record BaleOverview(string ApiBaseUrl, string WebhookBaseUrl, bool WebhookSecretSet, IReadOnlyDictionary<string, int> Bots);

public sealed record MessagingOverview(SmsOverview Sms, BaleOverview Bale);

/// <summary>One SMS. The text is never kept; <c>template</c> is set for template sends (OTPs).</summary>
public sealed record SmsLogItem(
    Guid Id, DateTimeOffset CreatedAt, string Purpose, Guid? WorkspaceId, string? WorkspaceName, string Phone, string Provider, string Status, string? Error,
    int Segments, string? Template);

public sealed record SmsTestRequest(string Phone);

/// <summary>A seller's Bale bot. <c>status</c>: active, reauth_required (the token stopped working) or disconnected.</summary>
public sealed record AdminBaleBot(
    Guid Id, Guid WorkspaceId, string? WorkspaceName, string BotId, string? Username, string? DisplayName, string Status, string? StatusReason,
    DateTimeOffset? StatusChangedAt, DateTimeOffset CreatedAt);

/// <summary><c>result</c>: ok (token works, webhook registered again), invalid_token (moved to reauth_required) or unavailable.</summary>
public sealed record BaleCheckResult(AdminBaleBot Bot, string Result);

/// <summary>
/// The Meta (Instagram) app as configured: ids and URLs, whether the secrets are set (never their values), the login scopes and
/// webhook fields, webhook results in the last 24 hours and when each was last seen, and sellers' Instagram accounts by status.
/// <c>tokensExpiringSoon</c>: active accounts whose token ends within 10 days.
/// </summary>
public sealed record MetaAppResponse(
    string AppId, string ApiVersion, string GraphBaseUrl, string RedirectUri, bool AppSecretSet, bool WebhookVerifyTokenSet, IReadOnlyList<string> Scopes,
    IReadOnlyList<string> WebhookFields, WebhookStats Webhooks, IReadOnlyDictionary<string, int> Accounts, int TokensExpiringSoon);

/// <summary>
/// SMS provider and Bale configuration and the SMS log in the AdminApi (admin plan AD-2.4). Readers: SuperAdmin, Support, ReadOnly.
/// A test SMS: SuperAdmin with step-up. Checking a Bale bot: Support.
/// </summary>
internal static class MessagingAdminEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapAdminApi();
        var read = api.MapGroup("/platform").WithTags("Platform")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));

        read.MapGet("/messaging", OverviewAsync)
            .WithSummary("SMS provider and Bale settings and their state")
            .Produces<MessagingOverview>();

        read.MapGet("/meta-app", MetaAppAsync)
            .WithSummary("The Meta app's settings, webhook health and Instagram accounts by status")
            .Produces<MetaAppResponse>();

        read.MapGet("/sms/log", LogAsync)
            .WithSummary("SMS the platform sent, newest first (kept 90 days)")
            .WithDescription("`phone` (any Iranian mobile format) finds one number's messages; `purpose` otp, invitation, notification, campaign, test or " +
                "other; `status` sent or failed; `workspaceId`.")
            .Produces<PagedResult<SmsLogItem>>()
            .WithStandardProblems();

        api.MapPost("/platform/sms/test", TestAsync)
            .WithTags("Platform")
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin))
            .RequireStepUp()
            .WithSummary("Send a test SMS through the provider")
            .WithDescription("SuperAdmin, step-up. 502 `sms.send_failed` with the provider's error. Audited as platform.sms.test.")
            .Produces<SmsLogItem>()
            .ProducesProblem(StatusCodes.Status502BadGateway)
            .WithStandardProblems();

        read.MapGet("/bale/bots", BotsAsync)
            .WithSummary("Sellers' Bale bots, newest first")
            .WithDescription("`status`; `q` matches the bot username; `workspaceId`.")
            .Produces<PagedResult<AdminBaleBot>>();

        api.MapPost("/platform/bale/bots/{id:guid}/check", CheckAsync)
            .WithTags("Platform")
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support))
            .WithSummary("Check a bot's token with Bale and register its webhook again")
            .WithDescription("Support or SuperAdmin. A token Bale refuses moves the bot to reauth_required (the seller is told to connect it again). " +
                "Audited as channels.bale.check.")
            .Produces<BaleCheckResult>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);
    }

    private static async Task<IResult> OverviewAsync(
        PlatformDbContext platform, ChannelsDbContext db, ISmsProviderInfo provider, IOptions<BaleOptions> bale, IClock clock, CancellationToken ct)
    {
        var since = clock.UtcNow.AddDays(-1);
        var rows = await platform.SmsLog.AsNoTracking().Where(s => s.CreatedAt >= since).GroupBy(s => new { s.Purpose, s.Status })
            .Select(g => new { g.Key.Purpose, g.Key.Status, Count = g.Count() }).ToListAsync(ct);
        var traffic = new SmsTraffic(
            rows.Where(r => r.Status == SmsLogEntry.Sent).Sum(r => r.Count),
            rows.Where(r => r.Status == SmsLogEntry.Failed).Sum(r => r.Count),
            rows.GroupBy(r => r.Purpose).ToDictionary(g => g.Key, g => g.Sum(r => r.Count)));
        var bots = await db.Accounts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(a => a.Kind == ChannelKinds.Bale)
            .GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var o = bale.Value;
        return Results.Ok(new MessagingOverview(
            new SmsOverview(await provider.StatusAsync(ct), traffic),
            new BaleOverview(o.ApiBaseUrl, o.WebhookBaseUrl, o.WebhookSecret != new BaleOptions().WebhookSecret && o.WebhookSecret.Length >= 16, bots)));
    }

    private static async Task<IResult> MetaAppAsync(
        ChannelsDbContext db, IOptions<InstagramOptions> instagram, [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys,
        IClock clock, CancellationToken ct)
    {
        var o = instagram.Value;
        var now = clock.UtcNow;
        var accounts = db.Accounts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(a => a.Kind == ChannelKinds.Instagram);
        var byStatus = await accounts.GroupBy(a => a.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var soon = now.AddDays(10);
        var expiring = await accounts.CountAsync(a => a.Status != ChannelStatuses.Disconnected && a.TokenExpiresAt != null && a.TokenExpiresAt < soon, ct);
        var stats = await InstagramWebhookStats.ReadAsync(redis.GetDatabase(), keys, now);
        return Results.Ok(new MetaAppResponse(
            o.AppId, o.ApiVersion, o.GraphBaseUrl, o.RedirectUri, !string.IsNullOrEmpty(o.AppSecret), !string.IsNullOrEmpty(o.WebhookVerifyToken), o.Scopes,
            o.WebhookFields, stats, byStatus, expiring));
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();

    private static async Task<IReadOnlyList<SmsLogItem>> ToItemsAsync(IReadOnlyList<SmsLogEntry> rows, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        foreach (var ws in rows.Where(r => r.WorkspaceId != null).Select(r => r.WorkspaceId!.Value).Distinct())
        {
            names[ws] = await workspaces.GetNameAsync(ws, ct);
        }

        return
        [
            .. rows.Select(r => new SmsLogItem(
                r.Id, r.CreatedAt, r.Purpose, r.WorkspaceId, r.WorkspaceId is { } w ? names.GetValueOrDefault(w) : null, r.PhoneMasked, r.Provider, r.Status, r.Error,
                r.Segments, r.Template)),
        ];
    }

    private static async Task<IResult> LogAsync(
        string? phone, string? purpose, string? status, Guid? workspaceId, int? page, int? pageSize, PlatformDbContext platform, IBlindIndexer indexer,
        IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var query = platform.SmsLog.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(phone))
        {
            if (!PhoneNumber.TryParse(phone, out var number))
            {
                return Invalid("phone", "validation.phone.invalid");
            }

            var bidx = indexer.Compute(number.Value.E164, SmsLogEntry.BlindIndexPurpose);
            query = query.Where(s => s.PhoneBidx == bidx);
        }

        if (purpose is { Length: > 0 })
        {
            if (!SmsPurposes.All.Contains(purpose))
            {
                return Invalid("purpose", "validation.invalid");
            }

            query = query.Where(s => s.Purpose == purpose);
        }

        if (status is { Length: > 0 })
        {
            if (status is not (SmsLogEntry.Sent or SmsLogEntry.Failed))
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(s => s.Status == status);
        }

        if (workspaceId is { } ws)
        {
            query = query.Where(s => s.WorkspaceId == ws);
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 50, 1, 100));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(s => s.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<SmsLogItem>(await ToItemsAsync(rows, workspaces, ct), total, p, size));
    }

    private static async Task<IResult> TestAsync(
        SmsTestRequest request, ISmsSender sms, PlatformDbContext platform, ChannelsDbContext db, IBlindIndexer indexer, IWorkspaceQueries workspaces,
        ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (!PhoneNumber.TryParse(request.Phone, out var number))
        {
            return Invalid("phone", "validation.phone.invalid");
        }

        var phone = number.Value;
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, null, caller.IpAddress), "platform.sms.test", "sms", phone.Masked, null, clock.UtcNow, after: new { phone = phone.Masked });
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);

        string? error = null;
        try
        {
            await sms.SendAsync(new SmsMessage(phone, "پیام آزمایشی پیشخوان", Purpose: SmsPurposes.Test), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            error = e.Message;
        }

        var bidx = indexer.Compute(phone.E164, SmsLogEntry.BlindIndexPurpose);
        var row = await platform.SmsLog.AsNoTracking().Where(s => s.PhoneBidx == bidx && s.Purpose == SmsPurposes.Test).OrderByDescending(s => s.CreatedAt).FirstOrDefaultAsync(ct);
        if (error is not null)
        {
            return Results.Problem(title: "sms.send_failed", detail: error, statusCode: StatusCodes.Status502BadGateway,
                extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = "sms.send_failed" });
        }

        return row is null
            ? Results.Ok(new SmsLogItem(Guid.Empty, clock.UtcNow, SmsPurposes.Test, null, null, phone.Masked, "unknown", SmsLogEntry.Sent, null, 1, null))
            : Results.Ok((await ToItemsAsync([row], workspaces, ct))[0]);
    }

    private static async Task<IReadOnlyList<AdminBaleBot>> ToBotsAsync(IReadOnlyList<Domain.ChannelAccount> rows, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        foreach (var ws in rows.Select(r => r.WorkspaceId).Distinct())
        {
            names[ws] = await workspaces.GetNameAsync(ws, ct);
        }

        return
        [
            .. rows.Select(a => new AdminBaleBot(
                a.Id, a.WorkspaceId, names.GetValueOrDefault(a.WorkspaceId), a.ExternalId, a.Username, a.DisplayName, a.Status, a.StatusReason, a.StatusChangedAt, a.CreatedAt)),
        ];
    }

    private static async Task<IResult> BotsAsync(
        string? status, string? q, Guid? workspaceId, int? page, int? pageSize, ChannelsDbContext db, IWorkspaceQueries workspaces, CancellationToken ct)
    {
        var query = db.Accounts.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(a => a.Kind == ChannelKinds.Bale);
        if (status is { Length: > 0 })
        {
            query = query.Where(a => a.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim().TrimStart('@');
            query = query.Where(a => a.Username != null && EF.Functions.ILike(a.Username, "%" + term + "%"));
        }

        if (workspaceId is { } ws)
        {
            query = query.Where(a => a.WorkspaceId == ws);
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 100));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.CreatedAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<AdminBaleBot>(await ToBotsAsync(rows, workspaces, ct), total, p, size));
    }

    private static async Task<IResult> CheckAsync(
        Guid id, ChannelsDbContext db, BaleBotClient bale, ISecretProtector secrets, IWorkspaceQueries workspaces, ICurrentUser caller, IClock clock,
        CancellationToken ct)
    {
        var account = await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(a => a.Id == id && a.Kind == ChannelKinds.Bale, ct);
        if (account is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (account.TokenEnc is null || account.Status == ChannelStatuses.Disconnected)
        {
            return Error.Conflict("channels.bale_disconnected").ToProblem();
        }

        db.Tenant.SetWorkspace(account.WorkspaceId);
        var token = secrets.Unprotect(account.TokenEnc, TokenPurposes.Bale);
        string result;
        try
        {
            await bale.GetMeAsync(token, ct);
            await bale.SetWebhookAsync(token, bale.WebhookUrl(account.Id), ct);
            result = "ok";
        }
        catch (BaleApiException e) when (e.Unauthorized)
        {
            account.RequireReauth(e.ErrorCode, clock.UtcNow);
            result = "invalid_token";
        }
        catch (BaleApiException)
        {
            result = "unavailable";
        }

        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, null, caller.IpAddress), "channels.bale.check", "channel_account", account.Id.ToString(), account.WorkspaceId, clock.UtcNow,
            after: new { result, account.Status });
        db.Publish(entry, entry.PartitionKey(), account.WorkspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new BaleCheckResult((await ToBotsAsync([account], workspaces, ct))[0], result));
    }
}
