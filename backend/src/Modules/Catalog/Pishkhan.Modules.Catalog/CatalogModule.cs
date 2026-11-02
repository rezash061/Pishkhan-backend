using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Catalog.Contracts;
using Pishkhan.Modules.Catalog.Features;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Catalog.Services;
using Pishkhan.Modules.Orders.Contracts;

namespace Pishkhan.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<CatalogDbContext>(CatalogDbContext.SchemaName);
        services.AddScoped<ICatalogQueries, CatalogQueries>();
        services.AddScoped<PriceChanges>();
        return services;
    }

    /// <summary>Commerce group: Excel imports, and stock taken and returned by orders.</summary>
    public static MessagingBuilder AddCatalogConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<CatalogImportRequested, CatalogImportHandler>("commerce")
            .AddConsumer<OrderStatusChanged, OrderStockHandler>("commerce");

    public static IEndpointRouteBuilder MapCatalogEndpoints(this IEndpointRouteBuilder app)
    {
        CatalogEndpoints.Map(app);
        PriceChangeEndpoints.Map(app);
        return app;
    }
}
