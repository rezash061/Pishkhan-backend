using System.Diagnostics;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Web.Http;

/// <summary>Maps unhandled exceptions to Problem Details. Never leaks exception messages for 5xx.</summary>
public sealed class ProblemExceptionHandler(IProblemDetailsService problems, ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, type, code, errors) = exception switch
        {
            ValidationException v => (400, ProblemTypes.Validation, "validation.failed", v.Errors.GroupBy(e => e.PropertyName).ToDictionary(g => g.Key, g => g.Select(e => e.ErrorCode).ToArray(), StringComparer.Ordinal)),
            DomainException d => (422, ProblemTypes.Unprocessable, d.Code, null),
            CrossTenantWriteException => (404, ProblemTypes.NotFound, "common.not_found", null),
            BadHttpRequestException b => (b.StatusCode, ProblemTypes.Validation, "request.invalid", null),
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => (409, ProblemTypes.Conflict, "common.concurrency_conflict", null),
            _ => (500, ProblemTypes.Failure, "common.internal_error", (Dictionary<string, string[]>?)null),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "Unhandled exception on {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;
        var problem = new Microsoft.AspNetCore.Mvc.ProblemDetails { Status = status, Type = type, Title = code };
        problem.Extensions["code"] = code;
        if (errors is not null)
        {
            problem.Extensions["errors"] = errors;
        }

        return await problems.TryWriteAsync(new ProblemDetailsContext { HttpContext = httpContext, ProblemDetails = problem, Exception = exception });
    }
}

public static class ProblemDetailsExtensions
{
    public static IServiceCollection AddPishkhanProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
            ctx.ProblemDetails.Extensions["traceId"] = Activity.Current?.TraceId.ToString() ?? ctx.HttpContext.TraceIdentifier;
            if (ctx.ProblemDetails.Type is null or "about:blank" || ctx.ProblemDetails.Type.StartsWith("https://tools.ietf.org", StringComparison.Ordinal))
            {
                ctx.ProblemDetails.Type = ctx.ProblemDetails.Status switch
                {
                    400 => ProblemTypes.Validation,
                    401 => ProblemTypes.Unauthorized,
                    403 => ProblemTypes.Forbidden,
                    404 => ProblemTypes.NotFound,
                    409 => ProblemTypes.Conflict,
                    413 => ProblemTypes.Validation,
                    415 => ProblemTypes.Unsupported,
                    429 => ProblemTypes.RateLimited,
                    _ => ProblemTypes.Failure,
                };
            }

            if (ctx.HttpContext.Features.Get<IHttpResponseFeature>() is { } response && response.StatusCode == 0)
            {
                response.StatusCode = ctx.ProblemDetails.Status ?? 500;
            }
        });
        services.AddExceptionHandler<ProblemExceptionHandler>();
        return services;
    }
}
