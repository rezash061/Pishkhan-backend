using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Pishkhan.Infrastructure.Telemetry;

public static class TelemetryExtensions
{
    /// <summary>
    /// Serilog (structured, PII-redacted, to console and OTLP) and OpenTelemetry traces and metrics over OTLP.
    /// Set <c>OpenTelemetry:Endpoint</c> to export; without it only the console logger is active.
    /// </summary>
    public static IHostApplicationBuilder AddPishkhanTelemetry(this IHostApplicationBuilder builder, string serviceName)
    {
        var endpoint = builder.Configuration["OpenTelemetry:Endpoint"];

        builder.Services.AddSerilog((services, logger) =>
        {
            logger.ReadFrom.Configuration(builder.Configuration)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", serviceName)
                .Enrich.With<PiiRedactionEnricher>()
                .Destructure.ToMaximumStringLength(4000)
                .WriteTo.Console(new Serilog.Formatting.Compact.RenderedCompactJsonFormatter())
                .Filter.ByExcluding(e => e.MessageTemplate.Text.Contains("{Password}", StringComparison.Ordinal));
            if (!string.IsNullOrEmpty(endpoint))
            {
                logger.WriteTo.OpenTelemetry(o =>
                {
                    o.Endpoint = endpoint;
                    o.ResourceAttributes = new Dictionary<string, object>(StringComparer.Ordinal) { ["service.name"] = serviceName };
                });
            }
        });

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(TelemetryExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(t =>
            {
                t.AddSource([.. PishkhanTelemetry.ActivitySourceNames])
                    .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                    .AddHttpClientInstrumentation();
            })
            .WithMetrics(m =>
            {
                m.AddMeter(PishkhanTelemetry.Name, "MassTransit", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "System.Net.Http", "Npgsql")
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            });

        if (!string.IsNullOrEmpty(endpoint))
        {
            otel.WithTracing(t => t.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)))
                .WithMetrics(m => m.AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)));
        }

        return builder;
    }
}
