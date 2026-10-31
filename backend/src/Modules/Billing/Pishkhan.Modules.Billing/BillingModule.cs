using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Billing.Features;
using Pishkhan.Modules.Billing.Persistence;

namespace Pishkhan.Modules.Billing;

/// <summary>Billing (backend plan 9.13, BE-1.11). For now: versioned plans, the back-office editor and the public price list.</summary>
public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BillingDbContext>(BillingDbContext.SchemaName);
        services.AddScoped<PlanAudit>();
        return services;
    }

    /// <summary>Api: <c>GET /api/v1/public/plans</c>.</summary>
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        PlanEndpoints.MapPublic(app);
        return app;
    }

    /// <summary>AdminApi: <c>/admin/v1/plans</c>. The host also needs the Backoffice module (staff names for the audit log).</summary>
    public static IEndpointRouteBuilder MapBillingAdminEndpoints(this IEndpointRouteBuilder app)
    {
        PlanEndpoints.MapAdmin(app);
        return app;
    }
}
