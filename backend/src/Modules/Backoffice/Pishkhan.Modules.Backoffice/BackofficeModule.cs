using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Features;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Backoffice;

public static class BackofficeModule
{
    public static IServiceCollection AddBackofficeModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<BackofficeOptions>().Bind(configuration.GetSection(BackofficeOptions.Section));
        services.AddModuleDbContext<BackofficeDbContext>(BackofficeDbContext.SchemaName);
        services.AddHandlersFrom(typeof(BackofficeModule).Assembly);
        services.AddScoped<IStaffProvisioning, StaffAuthHandlers>();
        return services;
    }

    public static IEndpointRouteBuilder MapBackofficeEndpoints(this IEndpointRouteBuilder app)
    {
        var anonymous = app.MapAdminAnonymousApi().WithTags("Staff auth");

        anonymous.MapPost("/auth/login", (StaffLoginRequest request, ICommandHandler<StaffLoginRequest, StaffLoginResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct)))
            .WithSummary("Staff sign-in with password and TOTP")
            .WithDescription("On first sign-in returns status totp_enrollment_required with a TOTP secret; confirm it with /auth/totp/enroll.")
            .Produces<StaffLoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithStandardProblems();

        anonymous.MapPost("/auth/totp/enroll", (TotpEnrollRequest request, ICommandHandler<TotpEnrollRequest, StaffLoginResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct)))
            .WithSummary("Confirm TOTP enrollment and start the session")
            .Produces<StaffLoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        anonymous.MapPost("/auth/refresh", (StaffRefreshRequest request, ICommandHandler<StaffRefreshRequest, StaffLoginResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct)))
            .WithSummary("Rotate the staff refresh token (15-minute idle, 8-hour absolute)")
            .Produces<StaffLoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        var admin = app.MapAdminApi().WithTags("Staff");

        admin.MapPost("/auth/logout", (ICommandHandler<StaffLogoutCommand, Unit> handler, CancellationToken ct) => Send(handler.HandleAsync(new StaffLogoutCommand(), ct), _ => Results.NoContent()))
            .WithSummary("End the staff session")
            .Produces(StatusCodes.Status204NoContent);

        admin.MapPost("/auth/step-up", (StepUpRequest request, ICommandHandler<StepUpRequest, StepUpResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct)))
            .WithSummary("Re-authenticate for a dangerous action; returns a 5-minute token")
            .Produces<StepUpResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        admin.MapGet("/me", (IQueryHandler<StaffMeQuery, StaffMeResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(new StaffMeQuery(), ct)))
            .WithSummary("Current staff member")
            .Produces<StaffMeResponse>();

        admin.MapGet("/staff", (IQueryHandler<ListStaffQuery, IReadOnlyList<StaffSummary>> handler, CancellationToken ct) => Send(handler.HandleAsync(new ListStaffQuery(), ct)))
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin))
            .WithSummary("Staff accounts")
            .Produces<IReadOnlyList<StaffSummary>>();

        admin.MapPost("/staff", (CreateStaffRequest request, ICommandHandler<CreateStaffRequest, StaffSummary> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct), r => Results.Created($"/admin/v1/staff/{r.Id}", r)))
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin))
            .RequireStepUp()
            .WithSummary("Create a staff account (step-up required)")
            .Produces<StaffSummary>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        return app;
    }

    private static async Task<IResult> Send<T>(Task<Result<T>> task, Func<T, IResult>? onSuccess = null) => (await task).ToHttp(onSuccess ?? (v => Results.Ok(v)));
}
