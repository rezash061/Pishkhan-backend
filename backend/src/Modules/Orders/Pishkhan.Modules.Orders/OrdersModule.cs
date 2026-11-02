using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Orders.Features;
using Pishkhan.Modules.Orders.Persistence;
using Pishkhan.Modules.Orders.Services;
using Pishkhan.Modules.Payments.Contracts;

namespace Pishkhan.Modules.Orders;

public static class OrdersModule
{
    public static IServiceCollection AddOrdersModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrdersDbContext>(OrdersDbContext.SchemaName);
        services.AddScoped<OrderPricing>();
        services.AddScoped<OrderPhones>();
        services.AddScoped<IOrderCommands, OrderCommands>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        return services;
    }

    /// <summary>Commerce group: verified payments move orders to paid.</summary>
    public static MessagingBuilder AddOrdersConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<PaymentConfirmed, PaymentConfirmedHandler>("commerce");

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder app)
    {
        OrderEndpoints.Map(app);
        return app;
    }
}
