using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Notifications.Features;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Notifications.Services;

namespace Pishkhan.Modules.Notifications;

/// <summary>
/// Notifications v1 (backend plan 9.15, BE-1.14): in-app list (live over SignalR), per-kind preferences, web push (VAPID) and SMS
/// for critical kinds (a handoff only once it has waited <see cref="NotificationsOptions.HandoffSmsAfter"/>).
/// </summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.SchemaName);
        services.AddScoped<NotificationWriter>();
        services.AddOptions<NotificationsOptions>().Bind(configuration.GetSection(NotificationsOptions.Section));
        services.AddOptions<WebPushOptions>().Bind(configuration.GetSection(WebPushOptions.Section));
        services.AddScoped<WebPushSender>();
        services.AddHttpClient(WebPushSender.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(Pishkhan.Infrastructure.Security.SafeHttpClient.CreateHandler);
        return services;
    }

    /// <summary>Platform group: notifications from handoffs, channel problems and explicit requests.</summary>
    public static MessagingBuilder AddNotificationsConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<NotificationRequested, NotificationRequestedHandler>("platform")
        .AddConsumer<HandoffRequested, HandoffNotificationHandler>("platform")
        .AddConsumer<ChannelStatusChanged, ChannelNotificationHandler>("platform")
        .AddConsumer<TimerFired, HandoffWaitingHandler>("platform");

    /// <summary>Api: <c>/workspaces/{workspaceId}/notifications</c>, <c>/notification-preferences</c> and <c>/me/push-subscriptions</c>.</summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        NotificationEndpoints.Map(app);
        PushEndpoints.Map(app);
        return app;
    }
}
