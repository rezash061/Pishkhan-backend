using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Automation.Engine;
using Pishkhan.Modules.Automation.Features;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;

namespace Pishkhan.Modules.Automation;

public static class AutomationModule
{
    public static IServiceCollection AddAutomationModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AutomationDbContext>(AutomationDbContext.SchemaName);
        services.AddScoped<AutomationCache>();
        services.AddScoped<FlowEngine>();
        return services;
    }

    /// <summary>Engine group: rules and flows for DMs and comments, automation timers, backlog runs.</summary>
    public static MessagingBuilder AddAutomationEngineConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<InboundMessageReceived, InboundEngineHandler>("engine")
        .AddConsumer<InboundCommentReceived, CommentEngineHandler>("engine")
        .AddConsumer<TimerFired, AutomationTimerHandler>("engine")
        .AddConsumer<BacklogPageRequested, BacklogPageHandler>("engine");

    /// <summary>Outbound group: public comment replies, retried while the account is rate limited.</summary>
    public static MessagingBuilder AddAutomationOutboundConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<CommentReplyRequested, CommentReplyHandler>(
            "outbound", new RetryPolicy(CommentReplyHandler.MaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)));

    /// <summary>Platform group: privacy requests.</summary>
    public static MessagingBuilder AddAutomationPlatformConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<DataDeletionRequested, AutomationDataDeletionHandler>("platform");

    public static IEndpointRouteBuilder MapAutomationEndpoints(this IEndpointRouteBuilder app)
    {
        AutomationEndpoints.Map(app);
        return app;
    }
}
