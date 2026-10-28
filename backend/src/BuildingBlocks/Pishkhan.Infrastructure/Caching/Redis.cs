using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.Caching;

public sealed class RedisOptions
{
    public const string Section = "Redis";

    /// <summary>Prefix <c>{env}:{schemaVersion}</c> of every key (backend plan section 5).</summary>
    public string KeyPrefix { get; set; } = "dev:v1";
}

/// <summary>
/// The two Redis deployments: <c>cache</c> (evicting, HybridCache L2, output cache) and <c>state</c> (non-evicting: OTPs, rate limits,
/// locks, idempotency keys, dedupe, SignalR backplane). Never mix them.
/// </summary>
public static class RedisInstances
{
    public const string Cache = "cache";
    public const string State = "state";
}

/// <summary>Builds keys with the environment prefix. Workspace-scoped keys must include the workspace id.</summary>
public sealed class RedisKeys(IOptions<RedisOptions> options)
{
    private readonly string _prefix = options.Value.KeyPrefix;

    public string Prefix => _prefix;

    public string For(string area, string rest) => $"{_prefix}:{area}:{rest}";

    public string ForWorkspace(string area, Guid workspaceId, string rest) => $"{_prefix}:{area}:{workspaceId:N}:{rest}";
}

public static class RedisExtensions
{
    public static IServiceCollection AddPishkhanRedis(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<RedisOptions>().Bind(configuration.GetSection(RedisOptions.Section));
        services.AddSingleton<RedisKeys>();
        services.AddKeyedSingleton<IConnectionMultiplexer>(RedisInstances.Cache, (_, _) => Connect(configuration, "RedisCache"));
        services.AddKeyedSingleton<IConnectionMultiplexer>(RedisInstances.State, (_, _) => Connect(configuration, "RedisState"));
        return services;
    }

    private static ConnectionMultiplexer Connect(IConfiguration configuration, string name)
    {
        var connectionString = configuration.GetConnectionString(name) ?? throw new InvalidOperationException($"ConnectionStrings:{name} is not configured.");
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        options.ClientName = "pishkhan";
        return ConnectionMultiplexer.Connect(options);
    }
}
