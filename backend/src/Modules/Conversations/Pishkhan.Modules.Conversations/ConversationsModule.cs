using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Features;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Conversations.Services;

namespace Pishkhan.Modules.Conversations;

public static class ConversationsModule
{
    public static IServiceCollection AddConversationsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ConversationsDbContext>(ConversationsDbContext.SchemaName);
        services.AddOptions<SpikeOptions>().Bind(configuration.GetSection(SpikeOptions.Section));
        return services;
    }

    public static MessagingBuilder AddConversationsIngestConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<InstagramWebhookReceived, InstagramIngestHandler>("ingest");

    public static MessagingBuilder AddConversationsEngineConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<InboundMessageReceived, EchoReplyHandler>("engine");

    public static MessagingBuilder AddConversationsOutboundConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<OutboundMessageRequested, OutboundSendHandler>(
            "outbound", new RetryPolicy(OutboundSendHandler.MaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60)));

    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        ConversationEndpoints.Map(app);
        return app;
    }
}
