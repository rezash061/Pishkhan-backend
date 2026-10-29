using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Features;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Conversations.Realtime;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.Modules.Conversations;

public static class ConversationsModule
{
    public static IServiceCollection AddConversationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ConversationsDbContext>(ConversationsDbContext.SchemaName);
        services.AddOptions<SpikeOptions>().Bind(configuration.GetSection(SpikeOptions.Section));
        services.AddScoped<IConversationQueries, ConversationQueries>();
        return services;
    }

    public static MessagingBuilder AddConversationsIngestConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<InstagramWebhookReceived, InstagramIngestHandler>("ingest");

    /// <summary>Platform group: privacy requests.</summary>
    public static MessagingBuilder AddConversationsPlatformConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<DataDeletionRequested, ConversationsDataDeletionHandler>("platform");

    /// <summary>Engine group: applies automation decisions; the M0 echo replies stay behind <c>Spike:EchoReplies</c>.</summary>
    public static MessagingBuilder AddConversationsEngineConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<InboundMessageReceived, EchoReplyHandler>("engine")
        .AddConsumer<ConversationActionsRequested, ConversationActionsHandler>("engine");

    public static MessagingBuilder AddConversationsOutboundConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<OutboundMessageRequested, OutboundSendHandler>(
            "outbound", new RetryPolicy(OutboundSendHandler.MaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)))
        .AddConsumer<TimerFired, OutboundDeferredHandler>("outbound");

    /// <summary>Api host: SignalR inbox (call after <c>AddPishkhanRealtime</c>).</summary>
    public static IServiceCollection AddConversationsRealtime(this IServiceCollection services)
    {
        services.AddSingleton<IInboxNotifier, InboxNotifier>();
        return services;
    }

    /// <summary>Api host, <c>realtime</c> group: pipeline events pushed to the panel. One replica handles each event; the backplane fans it out.</summary>
    public static MessagingBuilder AddConversationsRealtimeConsumers(this MessagingBuilder messaging) => messaging
        .AddHostConsumer<InboundMessageReceived, RealtimeInboundHandler>(RealtimeGroup)
        .AddHostConsumer<OutboundMessageRequested, RealtimeOutboundQueuedHandler>(RealtimeGroup)
        .AddHostConsumer<OutboundMessageCompleted, RealtimeOutboundCompletedHandler>(RealtimeGroup)
        .AddHostConsumer<MessagesRead, RealtimeReadHandler>(RealtimeGroup)
        .AddHostConsumer<HandoffRequested, RealtimeHandoffHandler>(RealtimeGroup)
        .AddHostConsumer<ChannelStatusChanged, RealtimeChannelStatusHandler>(RealtimeGroup);

    public const string RealtimeGroup = "realtime";

    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        ConversationEndpoints.Map(app);
        app.MapHub<InboxHub>(InboxHub.Path);
        return app;
    }
}
