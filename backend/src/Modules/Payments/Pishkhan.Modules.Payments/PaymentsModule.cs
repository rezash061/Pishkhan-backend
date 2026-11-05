using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.Modules.Payments.Features;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.Modules.Payments.Services;

namespace Pishkhan.Modules.Payments;

public static class PaymentsModule
{
    public static IServiceCollection AddPaymentsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<PaymentsDbContext>(PaymentsDbContext.SchemaName);
        services.AddOptions<PaymentsOptions>().Bind(configuration.GetSection(PaymentsOptions.Section));
        services.AddHttpClient<ZarinpalGateway>(c => c.Timeout = TimeSpan.FromSeconds(40))
            .AddStandardResilienceHandler(o =>
            {
                // A retried request would open a second payment at Zarinpal; the customer presses the button again instead.
                o.Retry.DisableForUnsafeHttpMethods();
                o.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                o.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
            });
        services.AddTransient<IPaymentGateway>(sp => sp.GetRequiredService<ZarinpalGateway>());
        services.AddTransient<Contracts.IZarinpalClient, ZarinpalClient>();
        services.AddScoped<MerchantSecrets>();
        services.AddScoped<PaymentFlow>();
        services.AddScoped<ReceiptExtractor>();
        services.AddScoped<ReceiptSubmission>();
        return services;
    }

    /// <summary>
    /// Commerce group: an order leaving awaiting_payment cancels its payment links; card-to-card receipts (BE-2.10) from customers' images,
    /// their scans and their checks.
    /// </summary>
    public static MessagingBuilder AddPaymentsConsumers(this MessagingBuilder messaging) =>
        messaging.AddConsumer<OrderStatusChanged, OrderStatusChangedHandler>("commerce")
            .AddConsumer<InboundMessageReceived, ReceiptMessageHandler>("commerce")
            .AddConsumer<FileScanCompleted, ReceiptScanHandler>("commerce")
            .AddConsumer<ReceiptSubmitted, ReceiptSubmittedHandler>("commerce");

    /// <summary>The panel's payment endpoints and the customer's payment pages (<c>/pay</c>).</summary>
    public static IEndpointRouteBuilder MapPaymentsEndpoints(this IEndpointRouteBuilder app)
    {
        PaymentEndpoints.Map(app);
        PayPages.Map(app);
        ReceiptEndpoints.Map(app);
        return app;
    }
}
