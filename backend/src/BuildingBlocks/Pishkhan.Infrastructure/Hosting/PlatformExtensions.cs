using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pishkhan.Infrastructure.Antivirus;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Locks;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.RateLimiting;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Infrastructure.Telemetry;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Hosting;

public static class PlatformExtensions
{
    /// <summary>The shared platform services every host needs. Connections are opened lazily, so build-time OpenAPI export works without infrastructure.</summary>
    public static IHostApplicationBuilder AddPishkhanPlatform(this IHostApplicationBuilder builder, string serviceName)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        builder.AddPishkhanTelemetry(serviceName);
        services.TryAddSingleton<IClock>(SystemClock.Instance);
        services.AddPishkhanPersistence(configuration);
        services.AddPishkhanRedis(configuration);
        services.AddPishkhanCaching();
        services.AddPishkhanStorage(configuration);
        services.AddPishkhanSecrets(configuration);
        services.AddPishkhanSms(configuration);
        services.AddPishkhanAntivirus(configuration);
        services.AddSafeHttpClient();
        services.TryAddSingleton<IDistributedLock, RedisDistributedLock>();
        services.TryAddSingleton<RedisSlidingWindow>();
        services.TryAddSingleton<RedisTokenBucket>();
        return builder;
    }
}
