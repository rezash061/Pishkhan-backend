using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Evals;
using Pishkhan.Modules.Agent.Persistence;

namespace Pishkhan.Modules.Agent;

public static class AgentModule
{
    public static IServiceCollection AddAgentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AgentDbContext>(AgentDbContext.SchemaName);
        services.AddPishkhanAi(configuration);
        services.AddScoped<EvalService>();
        return services;
    }
}
