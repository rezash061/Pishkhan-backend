using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Pishkhan.Web.Http;

public static class SecurityHeadersExtensions
{
    /// <summary>Headers for API hosts (backend plan 3.4). HSTS is added by <c>UseHsts</c> outside development.</summary>
    public static IApplicationBuilder UsePishkhanSecurityHeaders(this IApplicationBuilder app) => app.Use(async (context, next) =>
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers.XFrameOptions = "DENY";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            headers["Cross-Origin-Resource-Policy"] = "same-site";
            return Task.CompletedTask;
        });
        await next();
    });
}
