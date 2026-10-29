using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Content.Features;
using Pishkhan.Modules.Content.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Content;

/// <summary>Website content and public forms (backend plan 9.16). For now: leads (waitlist and contact form).</summary>
public static class ContentModule
{
    public static IServiceCollection AddContentModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<ContentDbContext>(ContentDbContext.SchemaName);
        services.AddOptions<AltchaOptions>().Bind(configuration.GetSection(AltchaOptions.Section));
        services.TryAddSingleton<AltchaVerifier>();
        services.AddHandlersFrom(typeof(ContentModule).Assembly);
        return services;
    }

    /// <summary>Api: <c>POST /api/v1/public/leads</c>.</summary>
    public static IEndpointRouteBuilder MapContentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPublicApi().WithTags("Website")
            .MapPost("/leads", (LeadRequest request, ICommandHandler<LeadRequest, LeadAccepted> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Json(r, statusCode: StatusCodes.Status201Created)))
            .WithSummary("Waitlist or contact form submission from the website")
            .WithDescription("Needs a solved ALTCHA challenge (single use). A repeat from the same phone within a day updates the earlier lead. Validation problems list fields under `errors`; a bad ALTCHA is `errors.altcha`.")
            .Produces<LeadAccepted>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .WithStandardProblems();
        return app;
    }

    /// <summary>AdminApi: <c>GET /admin/v1/leads</c> for sales and support follow-up.</summary>
    public static IEndpointRouteBuilder MapContentAdminEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapAdminApi().WithTags("Leads")
            .MapGet("/leads", ([AsParameters] ListLeadsQuery query, IQueryHandler<ListLeadsQuery, PagedResult<LeadResponse>> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(query, ct), r => Results.Ok(r)))
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support))
            .WithSummary("Website leads, newest first (SuperAdmin, Support)")
            .Produces<PagedResult<LeadResponse>>()
            .WithStandardProblems();
        return app;
    }

    private static async Task<IResult> Send<T>(Task<Result<T>> task, Func<T, IResult> onSuccess) => (await task).ToHttp(onSuccess);
}
