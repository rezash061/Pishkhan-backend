using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Audit.Features;
using Pishkhan.Modules.Audit.Persistence;
using Pishkhan.Modules.Audit.Services;

namespace Pishkhan.Modules.Audit;

public static class AuditModule
{
    public static IServiceCollection AddAuditModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<AuditDbContext>(AuditDbContext.SchemaName);
        return services;
    }

    /// <summary>Platform group: the audit sink.</summary>
    public static MessagingBuilder AddAuditConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<AuditEntryRecorded, AuditSink>("platform");

    /// <summary>AdminApi: <c>GET /admin/v1/audit</c>.</summary>
    public static IEndpointRouteBuilder MapAuditAdminEndpoints(this IEndpointRouteBuilder app)
    {
        AuditEndpoints.MapAdmin(app);
        return app;
    }

    /// <summary>Api: the workspace owner's view, <c>GET /api/v1/workspaces/{workspaceId}/audit</c>.</summary>
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        AuditEndpoints.MapPanel(app);
        return app;
    }
}
