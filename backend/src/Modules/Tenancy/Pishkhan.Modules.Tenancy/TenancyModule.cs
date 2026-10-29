using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Features;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.Modules.Tenancy.Services;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<TenancyOptions>().Bind(configuration.GetSection(TenancyOptions.Section));
        services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.SchemaName);
        services.AddScoped<IWorkspaceAccessResolver, WorkspaceAccessResolver>();
        services.AddScoped<IMembershipQueries, MembershipQueries>();
        services.AddScoped<IWorkspaceQueries, WorkspaceQueries>();
        services.AddHandlersFrom(typeof(TenancyModule).Assembly);
        return services;
    }

    public static IEndpointRouteBuilder MapTenancyEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapPanelApi().WithTags("Workspaces");

        api.MapPost("/workspaces", (CreateWorkspaceRequest request, ICommandHandler<CreateWorkspaceRequest, WorkspaceResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Created($"/api/v1/workspaces/{r.Id}", r)))
            .WithIdempotency()
            .WithSummary("Create a workspace; the caller becomes its owner")
            .Produces<WorkspaceResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems()
            .WithExamples(new CreateWorkspaceRequest("گالری لباس ترمه", "termeh-gallery", "clothing"));

        api.MapGet("/workspaces", (IQueryHandler<ListMyWorkspacesQuery, IReadOnlyList<WorkspaceResponse>> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new ListMyWorkspacesQuery(), ct), r => Results.Ok(r)))
            .WithSummary("Workspaces the caller belongs to")
            .Produces<IReadOnlyList<WorkspaceResponse>>();

        var ws = api.MapGroup("/workspaces/{workspaceId:guid}");

        ws.MapGet("/", (IQueryHandler<GetWorkspaceQuery, WorkspaceResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(new GetWorkspaceQuery(), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Workspace details")
            .Produces<WorkspaceResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPatch("/", (UpdateWorkspaceRequest request, ICommandHandler<UpdateWorkspaceCommand, WorkspaceResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new UpdateWorkspaceCommand(request), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Update workspace settings")
            .Produces<WorkspaceResponse>()
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .WithStandardProblems();

        ws.MapGet("/business-hours", (IQueryHandler<GetBusinessHoursQuery, BusinessHoursDto> handler, CancellationToken ct) => Send(handler.HandleAsync(new GetBusinessHoursQuery(), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Business hours (Asia/Tehran) and closed dates")
            .Produces<BusinessHoursDto>();

        ws.MapPut("/business-hours", (BusinessHoursDto request, ICommandHandler<SetBusinessHoursCommand, BusinessHoursDto> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new SetBusinessHoursCommand(request), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Replace business hours")
            .Produces<BusinessHoursDto>()
            .WithStandardProblems()
            .WithExamples(new BusinessHoursDto(BusinessHours.Default.Days, [new DateOnly(2027, 3, 21)]));

        ws.MapGet("/members", (IQueryHandler<ListMembersQuery, IReadOnlyList<MemberResponse>> handler, CancellationToken ct) => Send(handler.HandleAsync(new ListMembersQuery(), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.MembersRead)
            .WithSummary("Team members")
            .Produces<IReadOnlyList<MemberResponse>>();

        ws.MapPatch("/members/{userId:guid}", (Guid userId, ChangeRoleRequest request, ICommandHandler<ChangeRoleCommand, MemberResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new ChangeRoleCommand(userId, request.Role), ct), r => Results.Ok(r)))
            .RequireWorkspacePermission(Permissions.MembersManage)
            .WithSummary("Change a member's role")
            .Produces<MemberResponse>()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        ws.MapDelete("/members/{userId:guid}", (Guid userId, ICommandHandler<RemoveMemberCommand, Unit> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new RemoveMemberCommand(userId), ct), _ => Results.NoContent()))
            .RequireWorkspacePermission(Permissions.MembersManage)
            .WithSummary("Remove a member")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status409Conflict);

        ws.MapPost("/invitations", (InviteRequest request, ICommandHandler<InviteRequest, InvitationResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Created($"invitations/{r.Id}", r)))
            .RequireWorkspacePermission(Permissions.MembersManage)
            .WithIdempotency()
            .WithSummary("Invite a team member by phone (SMS link)")
            .Produces<InvitationResponse>(StatusCodes.Status201Created)
            .WithStandardProblems()
            .WithExamples(new InviteRequest("09120005678", WorkspaceRoles.Agent));

        api.MapPost("/invitations/{token}/accept", (string token, ICommandHandler<AcceptInvitationCommand, AcceptInvitationResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new AcceptInvitationCommand(token), ct), r => Results.Ok(r)))
            .WithTags("Workspaces")
            .WithSummary("Accept an invitation (signed in with the invited phone)")
            .Produces<AcceptInvitationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> Send<T>(Task<Result<T>> task, Func<T, IResult> onSuccess) => (await task).ToHttp(onSuccess);
}
