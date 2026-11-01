using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Features;
using Pishkhan.Modules.Orders.Persistence;
using Pishkhan.Modules.Orders.Services;

namespace Pishkhan.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrdersDbContext>(OrdersDbContext.SchemaName);
        services.AddScoped<OrderPricing>();
        services.AddScoped<OrderPhones>();
        services.AddScoped<IOrderCommands, OrderCommands>();
        return services;
    }

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        OrderEndpoints.Map(app);
        return app;
    }
}
