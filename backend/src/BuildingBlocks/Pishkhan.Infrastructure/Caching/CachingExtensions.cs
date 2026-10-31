using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.Caching;

public static class CachingExtensions
{
    /// <summary>HybridCache: L1 in-process memory and L2 on the <c>cache</c> Redis instance, with stampede protection.</summary>
    public static IServiceCollection AddPishkhanCaching(this IServiceCollection services)
    {
        services.AddSingleton<IConfigureOptions<RedisCacheOptions>>(sp => new ConfigureOptions<RedisCacheOptions>(o =>
        {
            var redis = sp.GetRequiredKeyedService<IConnectionMultiplexer>(RedisInstances.Cache);
            o.ConnectionMultiplexerFactory = () => Task.FromResult(redis);
            o.InstanceName = sp.GetRequiredService<RedisKeys>().Prefix + ":hc:";
        }));
        services.AddOptions<RedisCacheOptions>();

        // The plain RedisCache, not the one AddStackExchangeRedisCache registers: that one asks the container for HybridCache when it
        // first connects, which happens inside HybridCache's own constructor (it reads tag invalidations from L2). The re-entrant
        // resolution built a second HybridCache, so endpoints and the invalidation consumer held different instances and
        // invalidations from other hosts never reached what the endpoints read.
        services.AddSingleton<IDistributedCache>(sp => new RedisCache(sp.GetRequiredService<IOptions<RedisCacheOptions>>()));
        services.AddHybridCache(o =>
        {
            o.MaximumPayloadBytes = 1024 * 1024;
            o.DefaultEntryOptions = new HybridCacheEntryOptions { Expiration = TimeSpan.FromMinutes(10), LocalCacheExpiration = TimeSpan.FromMinutes(1) };
        });
        services.AddScoped<ICacheInvalidator, CacheInvalidator>();
        return services;
    }

    /// <summary>Every host receives cache invalidations on its own consumer group (broadcast).</summary>
    public static MessagingBuilder AddCacheInvalidation(this MessagingBuilder messaging) =>
        messaging.AddBroadcastConsumer<CacheInvalidated, CacheInvalidationHandler>("cache");
}

/// <summary>
/// Event-driven invalidation (backend plan section 5, rule 3): removes the tags in this process immediately and writes a
/// <see cref="CacheInvalidated"/> event to the outbox of the caller's transaction so every other host does the same.
/// </summary>
public interface ICacheInvalidator
{
    void Enqueue(ModuleDbContext db, params string[] tags);

    /// <summary>Call after the transaction committed, to drop the local entries right away.</summary>
    ValueTask InvalidateLocalAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default);
}

internal sealed class CacheInvalidator(HybridCache cache) : ICacheInvalidator
{
    public void Enqueue(ModuleDbContext db, params string[] tags)
    {
        if (tags.Length > 0)
        {
            db.Publish(new CacheInvalidated(tags), key: tags[0]);
        }
    }

    public ValueTask InvalidateLocalAsync(IEnumerable<string> tags, CancellationToken cancellationToken = default) => cache.RemoveByTagAsync(tags, cancellationToken);
}

public sealed class CacheInvalidationHandler(HybridCache cache) : IIntegrationEventHandler<CacheInvalidated>
{
    public async Task HandleAsync(CacheInvalidated message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        await cache.RemoveByTagAsync(message.Tags, cancellationToken);
}

/// <summary>Cache tags; one place so producers and readers agree.</summary>
public static class CacheTags
{
    public static string Workspace(Guid workspaceId) => $"ws:{workspaceId:N}";

    public static string Member(Guid workspaceId, Guid userId) => $"ws:{workspaceId:N}:member:{userId:N}";

    public const string Plans = "plans";

    public const string Content = "content";

    public const string AiRoutes = "ai:routes";
}
