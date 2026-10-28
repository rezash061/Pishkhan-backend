using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Caching;
using StackExchange.Redis;

namespace Pishkhan.Web.Auth;

/// <summary>Revoked session ids, kept until the longest access token of that session has expired (<c>sess:revoked:{sid}</c>).</summary>
public interface ISessionRevocationStore
{
    Task RevokeAsync(string scheme, string sessionId, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task<bool> IsRevokedAsync(string scheme, string sessionId, CancellationToken cancellationToken = default);
}

public sealed class RedisSessionRevocationStore([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys) : ISessionRevocationStore
{
    public Task RevokeAsync(string scheme, string sessionId, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().StringSetAsync(keys.For("sess", $"revoked:{scheme}:{sessionId}"), "1", ttl);

    public Task<bool> IsRevokedAsync(string scheme, string sessionId, CancellationToken cancellationToken = default) =>
        redis.GetDatabase().KeyExistsAsync(keys.For("sess", $"revoked:{scheme}:{sessionId}"));
}
