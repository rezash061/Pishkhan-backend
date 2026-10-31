using Microsoft.AspNetCore.Http;

namespace Pishkhan.Web.Auth;

/// <summary>Endpoint filter for dangerous actions: requires a step-up token issued in the last few minutes.</summary>
public static class StepUp
{
    public static TBuilder RequireStepUp<TBuilder>(this TBuilder builder)
        where TBuilder : Microsoft.AspNetCore.Builder.IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var claim = context.HttpContext.User.FindFirst("step_up_at")?.Value;
            if (claim is null || !long.TryParse(claim, System.Globalization.CultureInfo.InvariantCulture, out var at) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() - at > 300)
            {
                return Results.Problem(statusCode: 403, type: Http.ProblemTypes.Forbidden, title: "auth.step_up.required");
            }

            return await next(context);
        });
        return builder;
    }
}
