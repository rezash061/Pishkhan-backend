using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Web.Http;

namespace Pishkhan.Web.Auth;

public sealed record WorkspaceAccess(Guid WorkspaceId, string Role, IReadOnlySet<string> Permissions, bool Suspended);

/// <summary>Resolves the caller's membership in a workspace (cached). Implemented by the Tenancy module.</summary>
public interface IWorkspaceAccessResolver
{
    Task<WorkspaceAccess?> ResolveAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken);
}

public static class WorkspaceAccessExtensions
{
    public const string RouteParameter = "workspaceId";

    /// <summary>
    /// For routes under <c>/workspaces/{workspaceId}</c>: a non-member gets 404 (the workspace's existence is not revealed),
    /// a member without the permission gets 403. On success the tenant context is set for the request.
    /// </summary>
    public static TBuilder RequireWorkspacePermission<TBuilder>(this TBuilder builder, string permission)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
        {
            var http = context.HttpContext;
            if (!Guid.TryParse(http.GetRouteValue(RouteParameter)?.ToString(), out var workspaceId))
            {
                return SharedKernel.Error.NotFound().ToProblem();
            }

            var user = http.RequestServices.GetRequiredService<ICurrentUser>();
            var access = await http.RequestServices.GetRequiredService<IWorkspaceAccessResolver>().ResolveAsync(user.UserId, workspaceId, http.RequestAborted);
            if (access is null)
            {
                return SharedKernel.Error.NotFound().ToProblem();
            }

            if (!access.Permissions.Contains(permission))
            {
                return SharedKernel.Error.Forbidden().ToProblem();
            }

            http.RequestServices.GetRequiredService<ITenantContext>().SetWorkspace(workspaceId);
            http.Items[typeof(WorkspaceAccess)] = access;
            return await next(context);
        });
        return builder;
    }

    /// <summary>
    /// For actions a support session must not take (backend plan 3.4): account security (password, TOTP, other sessions), billing,
    /// deleting the workspace. Answers 403 <c>auth.support_session.not_allowed</c>.
    /// </summary>
    public static TBuilder DenySupportSession<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.AddEndpointFilter(async (context, next) =>
            context.HttpContext.RequestServices.GetRequiredService<ICurrentUser>().ActorStaffId is not null
                ? SharedKernel.Error.Forbidden("auth.support_session.not_allowed").ToProblem()
                : await next(context));
        return builder;
    }
}
