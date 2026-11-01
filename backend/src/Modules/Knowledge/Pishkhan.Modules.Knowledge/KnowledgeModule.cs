using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Knowledge.Features;
using Pishkhan.Modules.Knowledge.Persistence;
using Pishkhan.Modules.Knowledge.Services;

namespace Pishkhan.Modules.Knowledge;

public static class KnowledgeModule
{
    /// <summary>The AI gateway (embeddings) must be registered too: the Agent module adds it.</summary>
    public static IServiceCollection AddKnowledgeModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<KnowledgeDbContext>(KnowledgeDbContext.SchemaName);
        services.AddPishkhanAi(configuration);
        services.AddScoped<IKnowledgeSearch, KnowledgeSearch>();
        services.AddScoped<KnowledgeIndexer>();
        return services;
    }

    /// <summary>AI group: indexing of sources and of catalog products, and the agent's unanswered questions.</summary>
    public static MessagingBuilder AddKnowledgeConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<KnowledgeSourceChanged, KnowledgeSourceChangedHandler>("ai")
        .AddConsumer<ProductChanged, ProductKnowledgeHandler>("ai")
        .AddConsumer<UnansweredQuestionRaised, UnansweredQuestionHandler>("ai");

    public static IEndpointRouteBuilder MapKnowledgeEndpoints(this IEndpointRouteBuilder app)
    {
        KnowledgeEndpoints.Map(app);
        return app;
    }
}
