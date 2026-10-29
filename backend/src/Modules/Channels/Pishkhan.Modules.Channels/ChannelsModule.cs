using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Pishkhan.Infrastructure.Jobs;
using Pishkhan.Infrastructure.Messaging;
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
        services.AddSingleton<RateProfileProvider>();
        services.AddScoped<InstagramAccountGateway>();
        services.AddScoped<IChannelMessageSender, ChannelMessageSender>();
        services.AddScoped<IChannelProfileReader, ChannelProfileReader>();
        services.AddScoped<InstagramTokenRefresher>();
        services.AddSingleton<IChannelWebhookParser, InstagramWebhookParser>();
        return services;
    }

    /// <summary>Webhook ingress only (Ingress host): no database, just options, Redis and the Kafka producer.</summary>
    public static IServiceCollection AddChannelsIngress(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<InstagramOptions>().Bind(configuration.GetSection(InstagramOptions.Section));
        return services;
    }

    /// <summary>Recurring jobs (Workers host, platform group). Hourly: a token is refreshed days before expiry, so an hour of delay is harmless.</summary>
    public static readonly IReadOnlyList<RecurringJob> Jobs =
    [
        new(typeof(InstagramTokenRefreshJob), "channels.instagram-token-refresh", "0 15 * * * ?"),
    ];

    /// <summary>Platform group: bookkeeping of Meta data deletion requests.</summary>
    public static MessagingBuilder AddChannelsPlatformConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<DataDeletionCompleted, DataDeletionCompletedHandler>("platform");

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
