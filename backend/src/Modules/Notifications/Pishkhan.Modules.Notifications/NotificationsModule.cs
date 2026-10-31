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

/// <summary>Notifications v1 (backend plan 9.15, BE-1.14): in-app list, per-kind preferences, SMS for critical kinds.</summary>
public static class NotificationsModule
{
    public static IServiceCollection AddNotificationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<NotificationsDbContext>(NotificationsDbContext.SchemaName);
        services.AddScoped<NotificationWriter>();
        return services;
    }

    /// <summary>Platform group: notifications from handoffs, channel problems and explicit requests.</summary>
    public static MessagingBuilder AddNotificationsConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<NotificationRequested, NotificationRequestedHandler>("platform")
        .AddConsumer<HandoffRequested, HandoffNotificationHandler>("platform")
        .AddConsumer<ChannelStatusChanged, ChannelNotificationHandler>("platform");

    /// <summary>Api: <c>/workspaces/{workspaceId}/notifications</c> and <c>/notification-preferences</c>.</summary>
    public static IEndpointRouteBuilder MapNotificationsEndpoints(this IEndpointRouteBuilder app)
    {
        NotificationEndpoints.Map(app);
        return app;
    }
}
