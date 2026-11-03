using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.Modules.Templates.Features;
using Pishkhan.Modules.Templates.Persistence;
using Pishkhan.Modules.Templates.Services;

namespace Pishkhan.Modules.Templates;

/// <summary>
/// Industry templates (BE-2.9, gap G32): versioned starter content per industry (required facts, FAQs, flows, rules, agent profile), edited
/// in the back office and applied by a workspace at onboarding. Applying goes through the Knowledge, Automation and Agent contracts.
/// </summary>
public static class TemplatesModule
{
    /// <summary>Every host that reads templates (Api, AdminApi, Workers, Migrator).</summary>
    public static IServiceCollection AddTemplatesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<TemplatesDbContext>(TemplatesDbContext.SchemaName);
        services.AddScoped<ITemplateQueries, TemplateQueries>();
        services.AddScoped<ITemplateSeeder, TemplateSeeder>();
        return services;
    }

    /// <summary>Api: applying templates. Needs the Knowledge, Automation and Agent (replies) modules.</summary>
    public static IServiceCollection AddTemplatesApply(this IServiceCollection services)
    {
        services.AddScoped<TemplateApplier>();
        return services;
    }

    /// <summary>AdminApi: the editor. Needs the Automation template validator and the Backoffice staff directory.</summary>
    public static IServiceCollection AddTemplatesBackoffice(this IServiceCollection services)
    {
        services.AddScoped<TemplateAudit>();
        services.AddSingleton<TemplateContentValidator>();
        return services;
    }

    /// <summary>Api: <c>/workspaces/{id}/templates</c>.</summary>
    public static IEndpointRouteBuilder MapTemplatesEndpoints(this IEndpointRouteBuilder app)
    {
        TemplateEndpoints.MapPanel(app);
        return app;
    }

    /// <summary>AdminApi: <c>/admin/v1/industry-templates</c>.</summary>
    public static IEndpointRouteBuilder MapTemplatesAdminEndpoints(this IEndpointRouteBuilder app)
    {
        TemplateEndpoints.MapAdmin(app);
        return app;
    }
}
