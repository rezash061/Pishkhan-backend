using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Channels.Services;

internal static class TokenPurposes
{
    public const string Instagram = "channels.instagram-token";
}

internal static class ChannelStatusEvents
{
    /// <summary>
    /// Queues <see cref="ChannelStatusChanged"/> in the same transaction as the status change (panel banner, notifications), and an
    /// audit entry <c>channel.&lt;status&gt;</c>. <paramref name="actor"/> defaults to the system (token refresh, Meta webhooks).
    /// </summary>
    public static void PublishStatus(this ChannelsDbContext db, ChannelAccount account, AuditActor? actor = null)
    {
        db.Publish(new ChannelStatusChanged(account.Id, account.WorkspaceId, account.Status, account.StatusReason), account.Id.ToString(), account.WorkspaceId);
        var entry = AuditEntries.Create(
            actor ?? AuditActor.System, "channel." + account.Status, "channel_account", account.Id.ToString(), account.WorkspaceId, DateTimeOffset.UtcNow,
            after: new { account.Kind, account.Username, account.Status }, reason: account.StatusReason);
        db.Publish(entry, entry.PartitionKey(), account.WorkspaceId);
    }
}

internal sealed class ChannelDirectory(ChannelsDbContext db) : IChannelDirectory
{
    public Task<ChannelAccountInfo?> FindByExternalIdAsync(string kind, string externalId, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(a => a.Kind == kind && a.ExternalId == externalId && a.Status != ChannelStatuses.Disconnected)
            .Select(a => new ChannelAccountInfo(a.Id, a.WorkspaceId, a.Kind, a.ExternalId, a.Username, a.Status))
            .FirstOrDefaultAsync(cancellationToken);

    public Task<ChannelAccountInfo?> GetAsync(Guid id, CancellationToken cancellationToken) =>
        db.Accounts.AsNoTracking()
            .Where(a => a.Id == id)
            .Select(a => new ChannelAccountInfo(a.Id, a.WorkspaceId, a.Kind, a.ExternalId, a.Username, a.Status))
            .FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<ChannelAccountInfo>> ListAsync(CancellationToken cancellationToken) =>
        await db.Accounts.AsNoTracking()
            .Where(a => a.Status != ChannelStatuses.Disconnected)
            .OrderBy(a => a.CreatedAt)
            .Select(a => new ChannelAccountInfo(a.Id, a.WorkspaceId, a.Kind, a.ExternalId, a.Username, a.Status))
            .ToListAsync(cancellationToken);
}

/// <summary>Resolves a rate profile: an admin override in <c>channels.rate_profiles</c>, else configuration. Overrides are cached for a minute.</summary>
internal sealed class RateProfileProvider(IServiceScopeFactory scopes, IOptionsMonitor<InstagramOptions> options, IClock clock)
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, RateProfile? Profile)> _cache = new(StringComparer.Ordinal);

    public async Task<RateProfile> GetAsync(string name, CancellationToken ct)
    {
        var now = clock.UtcNow;
        if (!_cache.TryGetValue(name, out var cached) || now - cached.At > CacheFor)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
            var row = await db.RateProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.Name == name, ct);
            cached = (now, row is null ? null : FromValues(row.Values));
            _cache[name] = cached;
        }

        var configured = options.CurrentValue.RateProfiles;
        return cached.Profile ?? configured.GetValueOrDefault(name) ?? configured.GetValueOrDefault(RateProfile.Default) ?? new RateProfile();
    }

    public void Invalidate(string name) => _cache.TryRemove(name, out _);

    private static RateProfile FromValues(RateProfileValues v) => new()
    {
        Send = new RateBucket { Capacity = v.Send.Capacity, RefillPerSecond = v.Send.RefillPerSecond },
        PrivateReply = new RateBucket { Capacity = v.PrivateReply.Capacity, RefillPerSecond = v.PrivateReply.RefillPerSecond },
        CommentReply = new RateBucket { Capacity = v.CommentReply.Capacity, RefillPerSecond = v.CommentReply.RefillPerSecond },
        Read = new RateBucket { Capacity = v.Read.Capacity, RefillPerSecond = v.Read.RefillPerSecond },
    };
}

/// <summary>
/// Runs one Instagram API call for an account: checks the account is usable, takes a token from the account's bucket for the
/// API family (<c>rl:ig:{account}:{family}</c>) so bursts slow down instead of tripping Meta's limits, decrypts the token,
/// and maps failures. An invalid token moves the account to <c>reauth_required</c> and tells the owner.
/// </summary>
internal sealed class InstagramAccountGateway(
    ChannelsDbContext db,
    ISecretProtector secrets,
    RedisTokenBucket buckets,
    RateProfileProvider profiles,
    IClock clock,
    ILogger<InstagramAccountGateway> logger)
{
    public async Task<GatewayResult<T>> CallAsync<T>(Guid channelAccountId, string family, Func<string, Task<T>> call, CancellationToken ct)
    {
        var account = await db.Accounts.FirstOrDefaultAsync(a => a.Id == channelAccountId, ct);
        if (account is null || account.Status is ChannelStatuses.Disconnected or ChannelStatuses.ReauthRequired || account.TokenEnc is null)
        {
            return GatewayResult<T>.Failed("channel.unavailable", retryable: false);
        }

        if (account.Kind != ChannelKinds.Instagram)
        {
            return GatewayResult<T>.Failed("channel.kind_not_supported", retryable: false);
        }

        var bucket = (await profiles.GetAsync(account.RateProfile, ct)).For(family);
        var decision = await buckets.TryTakeAsync($"ig:{account.Id:N}:{family}", bucket.Capacity, bucket.RefillPerSecond);
        if (!decision.Allowed)
        {
            return GatewayResult<T>.Failed(ChannelErrors.RateLimited, retryable: true, decision.RetryAfter);
        }

        try
        {
            return GatewayResult<T>.Ok(await call(secrets.Unprotect(account.TokenEnc, TokenPurposes.Instagram)));
        }
        catch (InstagramApiException ex)
        {
            logger.LogWarning("Instagram {Family} call failed for account {ChannelAccountId}: {ErrorCode} ({Code}/{Subcode})", family, account.Id, ex.ErrorCode, ex.Code, ex.Subcode);
            if (ex.Kind == InstagramErrorKind.InvalidToken && account.RequireReauth(ex.ErrorCode, clock.UtcNow))
            {
                db.PublishStatus(account);
                await db.SaveChangesAsync(ct);
            }

            return GatewayResult<T>.Failed(ex.ErrorCode, ex.Retryable);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Instagram unreachable during {Family} call for account {ChannelAccountId}", family, account.Id);
            return GatewayResult<T>.Failed("instagram.unreachable", retryable: true);
        }
    }
}

internal sealed record GatewayResult<T>(bool Success, T? Value, string? ErrorCode, bool Retryable, TimeSpan? RetryAfter)
{
    public static GatewayResult<T> Ok(T value) => new(true, value, null, false, null);

    public static GatewayResult<T> Failed(string code, bool retryable, TimeSpan? retryAfter = null) => new(false, default, code, retryable, retryAfter);
}

internal sealed class ChannelMessageSender(InstagramAccountGateway gateway, InstagramGraphClient instagram) : IChannelMessageSender
{
    public async Task<SendResult> SendAsync(Guid channelAccountId, OutboundRecipient recipient, OutboundContent content, CancellationToken cancellationToken)
    {
        if (content.IsEmpty)
        {
            return SendResult.Failed("channel.empty_message", retryable: false);
        }

        var family = recipient.CommentId is null ? RateFamilies.Send : RateFamilies.PrivateReply;
        var body = InstagramMessageBuilder.Build(recipient, content);
        var result = await gateway.CallAsync(channelAccountId, family, token => instagram.SendAsync(token, body, cancellationToken), cancellationToken);
        return result.Success ? SendResult.Sent(result.Value) : SendResult.Failed(result.ErrorCode!, result.Retryable, result.RetryAfter);
    }

    public async Task<SendResult> ReplyToCommentAsync(Guid channelAccountId, string commentId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return SendResult.Failed("channel.empty_message", retryable: false);
        }

        var result = await gateway.CallAsync(channelAccountId, RateFamilies.CommentReply, token => instagram.ReplyToCommentAsync(token, commentId, text, cancellationToken), cancellationToken);
        return result.Success ? SendResult.Sent(result.Value) : SendResult.Failed(result.ErrorCode!, result.Retryable, result.RetryAfter);
    }
}

internal sealed class ChannelProfileReader(InstagramAccountGateway gateway, InstagramGraphClient instagram) : IChannelProfileReader
{
    public async Task<ChannelUserProfile?> GetProfileAsync(Guid channelAccountId, string externalUserId, CancellationToken cancellationToken)
    {
        var result = await gateway.CallAsync(channelAccountId, RateFamilies.Read, token => instagram.GetUserProfileAsync(token, externalUserId, cancellationToken), cancellationToken);
        if (result.Value is not { } p)
        {
            return null;
        }

        return new ChannelUserProfile(p.UserId, p.Name, p.Username, ToUri(p.ProfilePicture), p.FollowerCount, p.FollowsBusiness, p.BusinessFollowsUser);
    }

    internal static Uri? ToUri(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri : null;
}

internal sealed class ChannelMediaReader(InstagramAccountGateway gateway, InstagramGraphClient instagram) : IChannelMediaReader
{
    public async Task<ChannelReadResult<ChannelMedia>> GetMediaAsync(Guid channelAccountId, string mediaId, CancellationToken cancellationToken)
    {
        var result = await gateway.CallAsync(channelAccountId, RateFamilies.Read, token => instagram.GetMediaAsync(token, mediaId, cancellationToken), cancellationToken);
        if (result.Value is not { } m)
        {
            return ChannelReadResult<ChannelMedia>.Failed(result.ErrorCode ?? "channel.unavailable", result.Retryable);
        }

        return ChannelReadResult<ChannelMedia>.Ok(new ChannelMedia(
            m.Id, m.Caption, m.MediaType, ChannelProfileReader.ToUri(m.MediaUrl), ChannelProfileReader.ToUri(m.ThumbnailUrl), ChannelProfileReader.ToUri(m.Permalink), m.Timestamp));
    }

    public async Task<ChannelReadResult<ChannelCommentPage>> ListCommentsAsync(Guid channelAccountId, string mediaId, string? after, CancellationToken cancellationToken)
    {
        var result = await gateway.CallAsync(channelAccountId, RateFamilies.Read, token => instagram.ListCommentsAsync(token, mediaId, after, cancellationToken), cancellationToken);
        if (result.Value is not { } page)
        {
            return ChannelReadResult<ChannelCommentPage>.Failed(result.ErrorCode ?? "channel.unavailable", result.Retryable);
        }

        return ChannelReadResult<ChannelCommentPage>.Ok(new ChannelCommentPage(
            [.. page.Items.Select(c => new ChannelComment(c.Id, c.Text, c.FromId, c.Username, c.ParentId, c.Timestamp))], page.NextCursor));
    }
}

/// <summary>
/// Spacing that keeps a stream of calls inside the account's token buckets: one refill interval plus 10% headroom, so other traffic
/// and clock skew between replicas do not drain the bucket.
/// </summary>
internal sealed class ChannelPacingProvider(ChannelsDbContext db, RateProfileProvider profiles) : IChannelPacing
{
    private const double Headroom = 1.1;

    public async Task<ChannelPacing> GetAsync(Guid channelAccountId, CancellationToken cancellationToken)
    {
        using var system = db.Tenant.BeginSystemScope();
        var name = await db.Accounts.AsNoTracking().Where(a => a.Id == channelAccountId).Select(a => a.RateProfile).FirstOrDefaultAsync(cancellationToken);
        var profile = await profiles.GetAsync(name ?? RateProfile.Default, cancellationToken);
        return new ChannelPacing(Interval(profile.Send), Interval(profile.PrivateReply), Interval(profile.CommentReply));
    }

    private static TimeSpan Interval(RateBucket bucket) => TimeSpan.FromSeconds(Headroom / Math.Max(bucket.RefillPerSecond, 0.001));
}
