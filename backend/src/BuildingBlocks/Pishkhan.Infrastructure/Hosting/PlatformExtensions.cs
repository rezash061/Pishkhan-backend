using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
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
        AddSharedConfiguration(builder);
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

    /// <summary>
    /// Inserts <c>shared/appsettings.json</c> and <c>shared/appsettings.{Environment}.json</c> (copied from <c>backend/config</c>) as the
    /// lowest-priority sources, so a host's own appsettings, user secrets and environment variables still win.
    /// </summary>
    private static void AddSharedConfiguration(IHostApplicationBuilder builder)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "shared");
        if (!Directory.Exists(directory))
        {
            return;
        }

        var provider = new PhysicalFileProvider(directory);
        builder.Configuration.Sources.Insert(0, new JsonConfigurationSource { FileProvider = provider, Path = "appsettings.json", Optional = true, ReloadOnChange = false });
        builder.Configuration.Sources.Insert(1, new JsonConfigurationSource { FileProvider = provider, Path = $"appsettings.{builder.Environment.EnvironmentName}.json", Optional = true, ReloadOnChange = false });
    }
}
