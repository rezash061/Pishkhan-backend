using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Evals;
using Pishkhan.Modules.Agent.Features;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Agent.Replies;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Agent.Services;

namespace Pishkhan.Modules.Agent;

public static class AgentModule
{
    /// <summary>
    /// The agent module and the AI gateway. The gateway's settings are configuration merged with the back office's providers, routes
    /// and prompts (<see cref="AiSettingsStore"/>), reloaded every <c>Ai:Settings:RefreshSeconds</c>.
    /// </summary>
    public static IServiceCollection AddAgentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AgentDbContext>(AgentDbContext.SchemaName);
        services.AddOptions<AiSettingsOptions>().Bind(configuration.GetSection(AiSettingsOptions.Section));
        services.AddSingleton<AiSettingsStore>();
        services.AddSingleton<IAiSettings>(sp => sp.GetRequiredService<AiSettingsStore>());
        if (!Infrastructure.Hosting.BuildTime.IsActive)
        {
            // The build-time OpenAPI export starts the host without secrets or a database.
            services.AddHostedService<AiSettingsRefresher>();
        }
        services.AddPishkhanAi(configuration);
        services.AddScoped<EvalService>();
        services.AddScoped<ReplyPipeline>();
        return services;
    }

    /// <summary>AI group: the reply pipeline (<c>conversations.reply-requested.v1</c>).</summary>
    public static MessagingBuilder AddAgentConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<ReplyRequested, ReplyRequestedHandler>("ai");

    /// <summary>Api: the agent's settings and traces under <c>/workspaces/{id}/agent</c>.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        AgentEndpoints.Map(app);
        return app;
    }

    /// <summary>AdminApi, with <see cref="MapAgentAdminEndpoints"/>: the audit writer, which needs the Backoffice staff directory.</summary>
    public static IServiceCollection AddAgentBackoffice(this IServiceCollection services)
    {
        services.AddScoped<AiAudit>();
        return services;
    }

    /// <summary>AdminApi: <c>/admin/v1/ai</c> (providers, model routes, prompts with the eval gate, eval sets and runs).</summary>
    public static IEndpointRouteBuilder MapAgentAdminEndpoints(this IEndpointRouteBuilder app)
    {
        AiAdminEndpoints.Map(app);
        return app;
    }
}
