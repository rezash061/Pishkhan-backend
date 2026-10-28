using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Features;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;

namespace Pishkhan.Modules.Channels;

public static class ChannelsModule
{
    /// <summary>Full module: database, Graph client, sender and directory (Api and Workers hosts).</summary>
    public static IServiceCollection AddChannelsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ChannelsDbContext>(ChannelsDbContext.SchemaName);
        services.AddInstagramClient(configuration);
        services.AddScoped<IChannelDirectory, ChannelDirectory>();
        services.AddScoped<IChannelMessageSender, ChannelMessageSender>();
        services.AddSingleton<IChannelWebhookParser, InstagramWebhookParser>();
        return services;
    }

    /// <summary>Webhook ingress only (Ingress host): no database, just options, Redis and the Kafka producer.</summary>
    public static IServiceCollection AddChannelsIngress(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InstagramOptions>().Bind(configuration.GetSection(InstagramOptions.Section));
        return services;
    }

    public static IEndpointRouteBuilder MapChannelsEndpoints(this IEndpointRouteBuilder app)
    {
        ChannelEndpoints.Map(app);
        MetaCallbacks.Map(app);
        return app;
    }

    public static IEndpointRouteBuilder MapChannelsIngress(this IEndpointRouteBuilder app)
    {
        InstagramWebhooks.MapIngress(app);
        return app;
    }

    private static void AddInstagramClient(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InstagramOptions>().Bind(configuration.GetSection(InstagramOptions.Section));
        services.AddHttpClient<InstagramGraphClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
            .AddStandardResilienceHandler(o =>
            {
                // A retried POST could send a DM twice; the outbound consumer decides about retries instead.
                o.Retry.DisableForUnsafeHttpMethods();
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            });
    }
}
