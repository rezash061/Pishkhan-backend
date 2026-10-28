using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Hosting;
using Pishkhan.Web.Http;
using Serilog;

namespace Pishkhan.Web.Hosting;

/// <summary>The common setup of the HTTP hosts (Api, AdminApi, Ingress).</summary>
public static class WebHostExtensions
{
    public static IHealthChecksBuilder AddPishkhanWebHost(this WebApplicationBuilder builder, string serviceName)
    {
        builder.AddPishkhanPlatform(serviceName);
        var services = builder.Services;
        services.AddPishkhanProblemDetails();
        services.AddPishkhanRateLimiting(builder.Configuration);
        services.Configure<ForwardedHeadersOptions>(o =>
        {
            // The edge proxy (backend plan 3.7) is the only hop; trust its X-Forwarded-* headers.
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
        });
        builder.WebHost.ConfigureKestrel(k =>
        {
            k.AddServerHeader = false;
            k.Limits.MaxRequestBodySize = 2 * 1024 * 1024; // raised per endpoint (uploads)
        });
        return services.AddHealthChecks();
    }

    public static WebApplication UsePishkhanWebHost(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UsePishkhanSecurityHeaders();
        app.UseSerilogRequestLogging(o => o.GetLevel = (ctx, _, ex) =>
            ex is not null || ctx.Response.StatusCode >= 500 ? Serilog.Events.LogEventLevel.Error
            : ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal) ? Serilog.Events.LogEventLevel.Verbose
            : Serilog.Events.LogEventLevel.Information);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseRateLimiter();
        app.UsePishkhanIdempotency();
        app.MapPishkhanHealth();
        return app;
    }

    /// <summary><c>/health/live</c> (process up, no dependencies) and <c>/health/ready</c> (dependencies tagged ready).</summary>
    public static IEndpointRouteBuilder MapPishkhanHealth(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().ExcludeFromDescription();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = c => c.Tags.Contains(HealthTags.Ready),
            ResponseWriter = WriteAsync,
        }).AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private static Task WriteAsync(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => new { status = e.Value.Status.ToString(), ms = (int)e.Value.Duration.TotalMilliseconds }, StringComparer.Ordinal),
        }));
    }
}
