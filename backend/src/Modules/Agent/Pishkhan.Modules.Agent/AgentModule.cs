using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pishkhan.AI;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
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
        services.AddOptions<DemoChatOptions>().Bind(configuration.GetSection(DemoChatOptions.Section));
        if (!services.Any(d => d.ServiceType == typeof(AltchaVerifier)))
        {
            services.AddOptions<AltchaOptions>().Bind(configuration.GetSection(AltchaOptions.Section));
            services.TryAddSingleton<AltchaVerifier>();
        }

        return services;
    }

    /// <summary>
    /// Api and Workers: the reply pipeline (test mode, demo chat, AI replies). It reads the Knowledge and Catalog modules, so the host
    /// must have them; the AdminApi does not.
    /// </summary>
    public static IServiceCollection AddAgentReplies(this IServiceCollection services)
    {
        services.AddScoped<ReplyPipeline>();
        services.AddScoped<IAgentTemplates, AgentTemplates>();
        return services;
    }

    /// <summary>AI group: the reply pipeline (<c>conversations.reply-requested.v1</c>).</summary>
    public static MessagingBuilder AddAgentConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<ReplyRequested, ReplyRequestedHandler>("ai");

    /// <summary>Api: the agent's settings, traces and test mode under <c>/workspaces/{id}/agent</c>, and the website's <c>/public/demo-chat</c>.</summary>
    public static IEndpointRouteBuilder MapAgentEndpoints(this IEndpointRouteBuilder app)
    {
        AgentEndpoints.Map(app);
        AgentSandbox.Map(app);
        DemoChat.Map(app);
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
