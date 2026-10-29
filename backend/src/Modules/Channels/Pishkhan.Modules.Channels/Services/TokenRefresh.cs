using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.SharedKernel;
using Quartz;

namespace Pishkhan.Modules.Channels.Services;

public sealed record TokenRefreshReport(int Refreshed, int Failed, int StatusChanges);

/// <summary>
/// Refreshes long-lived Instagram tokens before they expire (60-day tokens, refreshed inside <see cref="InstagramOptions.RefreshBefore"/>).
/// Revoked tokens move the account to <c>reauth_required</c>; repeated transient failures close to expiry move it to <c>token_expiring</c>.
/// Every status change emits <c>channels.channel-status-changed.v1</c>.
/// </summary>
public sealed class InstagramTokenRefresher(
    ChannelsDbContext db,
    ITenantContext tenant,
    InstagramGraphClient instagram,
    ISecretProtector secrets,
    IOptions<InstagramOptions> options,
    IClock clock,
    ILogger<InstagramTokenRefresher> logger)
{
    public const int BatchSize = 200;

    public async Task<TokenRefreshReport> RunAsync(CancellationToken ct)
    {
        using var system = tenant.BeginSystemScope();
        var now = clock.UtcNow;
        var o = options.Value;
        var horizon = now.Add(o.RefreshBefore);
        var candidates = await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(a => a.Kind == ChannelKinds.Instagram
                && (a.Status == ChannelStatuses.Active || a.Status == ChannelStatuses.TokenExpiring)
                && a.TokenExpiresAt != null && a.TokenExpiresAt <= horizon)
            .OrderBy(a => a.TokenExpiresAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        int refreshed = 0, failed = 0, changes = 0;
        foreach (var account in candidates)
        {
            bool changed;
            if (account.TokenExpiresAt <= now)
            {
                changed = account.TokenRefreshFailed("instagram.token_expired", now, o.ExpiringWarning);
                failed++;
            }
            else if (!account.IsDueForRefresh(now, o.RefreshBefore))
            {
                continue;
            }
            else
            {
                try
                {
                    var token = await instagram.RefreshAsync(secrets.Unprotect(account.TokenEnc!, TokenPurposes.Instagram), ct);
                    changed = account.TokenRefreshed(secrets.Protect(token.AccessToken, TokenPurposes.Instagram), now.AddSeconds(token.ExpiresInSeconds), now);
                    refreshed++;
                }
                catch (InstagramApiException ex) when (ex.Kind == InstagramErrorKind.InvalidToken)
                {
                    changed = account.RequireReauth(ex.ErrorCode, now);
                    failed++;
                }
                catch (Exception ex) when (ex is InstagramApiException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Token refresh for {ChannelAccountId} failed: {Error}", account.Id, ex.Message);
                    changed = account.TokenRefreshFailed(ex is InstagramApiException api ? api.ErrorCode : "instagram.unreachable", now, o.ExpiringWarning);
                    failed++;
                }
            }

            if (changed)
            {
                db.PublishStatus(account);
                changes++;
            }

            // One save per account: a crash midway keeps the tokens already refreshed (the old one stops working once refreshed).
            await db.SaveChangesAsync(ct);
        }

        if (refreshed + failed > 0)
        {
            logger.LogInformation("Instagram token refresh: {Refreshed} refreshed, {Failed} failed, {Changes} status change(s)", refreshed, failed, changes);
        }

        return new TokenRefreshReport(refreshed, failed, changes);
    }
}

[DisallowConcurrentExecution]
public sealed class InstagramTokenRefreshJob(InstagramTokenRefresher refresher) : IJob
{
    public Task Execute(IJobExecutionContext context) => refresher.RunAsync(context.CancellationToken);
}
