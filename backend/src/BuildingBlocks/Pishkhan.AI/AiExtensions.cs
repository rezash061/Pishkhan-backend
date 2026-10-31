using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pishkhan.SharedKernel;

namespace Pishkhan.AI;

public static class AiExtensions
{
    public static IServiceCollection AddPishkhanAi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IAiUsageSink, MetricsUsageSink>();
        services.AddSingleton<CircuitBreakers>();
        services.TryAddSingleton<IAiSettings, ConfigAiSettings>();
        services.AddSingleton<ModelRouter>();
        services.AddSingleton<IModelRouter>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<IAiProviderProbe>(sp => sp.GetRequiredService<ModelRouter>());
        services.AddSingleton<IPromptRegistry, ConfigPromptRegistry>();

        // Per-route timeouts are applied by the router; the HttpClient only bounds a hung connection.
        services.AddHttpClient(ModelRouter.HttpClientName, c => c.Timeout = TimeSpan.FromMinutes(3));
        return services;
    }
}
