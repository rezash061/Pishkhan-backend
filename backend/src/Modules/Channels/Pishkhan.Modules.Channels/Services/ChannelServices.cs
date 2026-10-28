using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;

namespace Pishkhan.Modules.Channels.Services;

internal static class TokenPurposes
{
    public const string Instagram = "channels.instagram-token";
}

internal sealed class ChannelDirectory(ChannelsDbContext db) : IChannelDirectory
{
    public Task<ChannelAccountInfo?> FindByExternalIdAsync(string kind, string externalId, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(a => a.Kind == kind && a.ExternalId == externalId && a.Status != ChannelStatuses.Disconnected)
            .Select(a => new ChannelAccountInfo(a.Id, a.WorkspaceId, a.Kind, a.ExternalId, a.Username, a.Status))
            .FirstOrDefaultAsync(cancellationToken);
}

/// <summary>
/// Sends through the account's channel, paced by a per-account token bucket (<c>rl:ig:{account}:send</c>) so bursts slow down
/// instead of tripping Meta's limits. An invalid token moves the account to <c>reauth_required</c> and tells the owner.
/// </summary>
internal sealed class ChannelMessageSender(
    ChannelsDbContext db,
    InstagramGraphClient instagram,
    ISecretProtector secrets,
    RedisTokenBucket buckets,
    IOptions<InstagramOptions> options,
    ILogger<ChannelMessageSender> logger) : IChannelMessageSender
{
    public async Task<SendResult> SendAsync(Guid channelAccountId, string recipientExternalId, OutboundContent content, CancellationToken cancellationToken)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == channelAccountId, cancellationToken);
        if (account is null || account.Status is ChannelStatuses.Disconnected or ChannelStatuses.ReauthRequired || account.TokenEnc is null)
        {
            return SendResult.Failed("channel.unavailable", retryable: false);
        }

        if (account.Kind != ChannelKinds.Instagram)
        {
            return SendResult.Failed("channel.kind_not_supported", retryable: false);
        }

        if (string.IsNullOrWhiteSpace(content.Text))
        {
            return SendResult.Failed("channel.empty_message", retryable: false);
        }

        var profile = options.Value.ProfileFor(account.RateProfile);
        var decision = await buckets.TryTakeAsync($"ig:{account.Id:N}:send", profile.Capacity, profile.RefillPerSecond);
        if (!decision.Allowed)
        {
            return SendResult.Failed("channel.rate_limited", retryable: true);
        }

        try
        {
            var token = secrets.Unprotect(account.TokenEnc, TokenPurposes.Instagram);
            var messageId = await instagram.SendTextAsync(token, recipientExternalId, content.Text, cancellationToken);
            return SendResult.Sent(messageId);
        }
        catch (InstagramApiException ex)
        {
            logger.LogWarning("Instagram send failed for account {ChannelAccountId}: {ErrorCode} ({Code}/{Subcode})", account.Id, ex.ErrorCode, ex.Code, ex.Subcode);
            if (ex.Kind == InstagramErrorKind.InvalidToken)
            {
                account.RequireReauth();
                db.Publish(new ChannelStatusChanged(account.Id, account.WorkspaceId, account.Status, ex.ErrorCode), account.Id.ToString(), account.WorkspaceId);
                await db.SaveChangesAsync(cancellationToken);
            }

            return SendResult.Failed(ex.ErrorCode, ex.Retryable);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Instagram unreachable while sending for account {ChannelAccountId}", account.Id);
            return SendResult.Failed("instagram.unreachable", retryable: true);
        }
    }
}
