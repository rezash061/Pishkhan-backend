using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Features;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Modules.Billing.Services;

namespace Pishkhan.Modules.Billing;

/// <summary>
/// Billing (backend plan 9.13, BE-1.11): versioned plans and the public price list, subscriptions granted by staff, usage per period
/// and entitlements (<see cref="IEntitlements"/>) for other modules. Paid checkout, invoices and overage are Phase 2.
/// </summary>
public static class BillingModule
{
    public static IServiceCollection AddBillingModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BillingDbContext>(BillingDbContext.SchemaName);
        services.AddSingleton<IEntitlements, EntitlementService>();
        return services;
    }

    /// <summary>AdminApi, with <see cref="MapBillingAdminEndpoints"/>: the audit writers, which need the Backoffice staff directory.</summary>
    public static IServiceCollection AddBillingBackoffice(this IServiceCollection services)
    {
        services.AddScoped<PlanAudit>();
        services.AddScoped<SubscriptionAudit>();
        return services;
    }

    /// <summary>Workers (platform group): counts <c>billing.usage-recorded</c> events into the period's usage.</summary>
    public static MessagingBuilder AddBillingConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<UsageRecorded, UsageRecordedHandler>("platform");

    /// <summary>Api: <c>GET /api/v1/public/plans</c> and the panel's <c>/workspaces/{id}/billing</c>.</summary>
    public static IEndpointRouteBuilder MapBillingEndpoints(this IEndpointRouteBuilder app)
    {
        PlanEndpoints.MapPublic(app);
        SubscriptionEndpoints.MapPanel(app);
        return app;
    }

    /// <summary>
/// AdminApi: <c>/admin/v1/plans</c> and the workspace billing endpoints. The host also needs the Backoffice module (staff names for
/// the audit log), Tenancy and the channels directory.
/// </summary>
    public static IEndpointRouteBuilder MapBillingAdminEndpoints(this IEndpointRouteBuilder app)
    {
        PlanEndpoints.MapAdmin(app);
        SubscriptionEndpoints.MapAdmin(app);
        return app;
    }
}
