using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Analytics.Features;
using Pishkhan.Modules.Analytics.Persistence;
using Pishkhan.Modules.Analytics.Services;
using Pishkhan.Modules.Conversations.Contracts;

namespace Pishkhan.Modules.Analytics;

/// <summary>Activity rollups and the dashboard (backend plan 9.17, BE-1.10).</summary>
public static class AnalyticsModule
{
    public static IServiceCollection AddAnalyticsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AnalyticsDbContext>(AnalyticsDbContext.SchemaName);
        return services;
    }

    /// <summary>Platform group: hourly counts of messages, replies and handoffs.</summary>
    public static MessagingBuilder AddAnalyticsConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<InboundMessageReceived, InboundMetricsHandler>("platform")
        .AddConsumer<OutboundMessageRequested, ReplyMetricsHandler>("platform")
        .AddConsumer<HandoffRequested, HandoffMetricsHandler>("platform");

    /// <summary>Api: <c>GET /api/v1/workspaces/{workspaceId}/dashboard</c>.</summary>
    public static IEndpointRouteBuilder MapAnalyticsEndpoints(this IEndpointRouteBuilder app)
    {
        Dashboard.Map(app);
        return app;
    }
}
