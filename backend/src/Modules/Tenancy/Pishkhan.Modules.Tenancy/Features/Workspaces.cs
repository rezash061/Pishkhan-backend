using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Tenancy.Features;

public sealed record CreateWorkspaceRequest(string Name, string? Slug, string? Industry);

public sealed record UpdateWorkspaceRequest(string? Name, string? Industry);

/// <summary>
/// <c>status</c>: active, suspended, pending_deletion. A workspace pending deletion is listed (so its owner can cancel) but its
/// other endpoints answer 404; <c>deletionScheduledFor</c> is when its data is deleted.
/// </summary>
public sealed record WorkspaceResponse(
    Guid Id, string Name, string Slug, string Industry, string TimeZone, string Status, string Role, DateTimeOffset CreatedAt, DateTimeOffset? DeletionScheduledFor);

public sealed record ListMyWorkspacesQuery;

public sealed record GetWorkspaceQuery;

public sealed record UpdateWorkspaceCommand(UpdateWorkspaceRequest Request);

public sealed record BusinessHoursDto(IReadOnlyList<DayHours> Days, IReadOnlyList<DateOnly> ClosedDates);

public sealed record GetBusinessHoursQuery;

public sealed record SetBusinessHoursCommand(BusinessHoursDto Hours);

internal sealed class CreateWorkspaceValidator : AbstractValidator<CreateWorkspaceRequest>
{
    public CreateWorkspaceValidator()
    {
        RuleFor(x => x.Name).NotEmpty().WithErrorCode("validation.required").MaximumLength(100).WithErrorCode("validation.too_long");
        RuleFor(x => x.Slug!).Must(s => Workspace.SlugPattern().IsMatch(s)).When(x => x.Slug is not null).WithErrorCode("validation.slug.invalid");
        RuleFor(x => x.Industry!).Must(i => Workspace.Industries.Contains(i)).When(x => x.Industry is not null).WithErrorCode("validation.industry.invalid");
    }
}

internal sealed class UpdateWorkspaceValidator : AbstractValidator<UpdateWorkspaceCommand>
{
    public UpdateWorkspaceValidator()
    {
        RuleFor(x => x.Request.Name!).NotEmpty().WithErrorCode("validation.required").MaximumLength(100).WithErrorCode("validation.too_long").When(x => x.Request.Name is not null);
        RuleFor(x => x.Request.Industry!).Must(i => Workspace.Industries.Contains(i)).When(x => x.Request.Industry is not null).WithErrorCode("validation.industry.invalid");
    }
}

internal sealed class SetBusinessHoursValidator : AbstractValidator<SetBusinessHoursCommand>
{
    public SetBusinessHoursValidator()
    {
        RuleFor(x => x.Hours.Days).NotNull().Must(d => d.Count <= 7 && d.Select(x => x.Day).Distinct().Count() == d.Count).WithErrorCode("validation.business_hours.days");
        RuleForEach(x => x.Hours.Days).Must(d => d.Ranges.Count <= 4 && d.Ranges.All(r => r.From < r.To)).WithErrorCode("validation.business_hours.range");
        RuleFor(x => x.Hours.ClosedDates).Must(c => c.Count <= 400).WithErrorCode("validation.too_many");
    }
}

internal sealed class WorkspaceHandlers(TenancyDbContext db, ITenantContext tenant, ICurrentUser caller, ICacheInvalidator cache, IClock clock) :
    ICommandHandler<CreateWorkspaceRequest, WorkspaceResponse>,
    IQueryHandler<ListMyWorkspacesQuery, IReadOnlyList<WorkspaceResponse>>,
    IQueryHandler<GetWorkspaceQuery, WorkspaceResponse>,
    ICommandHandler<UpdateWorkspaceCommand, WorkspaceResponse>,
    IQueryHandler<GetBusinessHoursQuery, BusinessHoursDto>,
    ICommandHandler<SetBusinessHoursCommand, BusinessHoursDto>
{
    public async Task<Result<WorkspaceResponse>> HandleAsync(CreateWorkspaceRequest command, CancellationToken cancellationToken)
    {
        var slug = command.Slug ?? Workspace.SuggestSlug(command.Name);
        if (await db.Workspaces.AnyAsync(w => w.Slug == slug, cancellationToken))
        {
            return Error.Conflict("tenancy.slug.taken");
        }

        var now = clock.UtcNow;
        var workspace = Workspace.Create(command.Name, slug, command.Industry ?? "other", now);
        tenant.SetWorkspace(workspace.Id);
        var membership = Membership.Create(workspace.Id, caller.UserId, WorkspaceRoles.Owner, now);
        db.Workspaces.Add(workspace);
        db.Memberships.Add(membership);
        db.Publish(new WorkspaceChanged(workspace.Id, "created"), key: workspace.Id.ToString());
        db.Publish(new MembershipChanged(workspace.Id, caller.UserId, WorkspaceRoles.Owner, false), key: workspace.Id.ToString());
        Audit("workspace.create", "workspace", workspace.Id, after: new { workspace.Name, workspace.Slug, workspace.Industry });
        await db.SaveChangesAsync(cancellationToken);
        return ToResponse(workspace, WorkspaceRoles.Owner);
    }

    public async Task<Result<IReadOnlyList<WorkspaceResponse>>> HandleAsync(ListMyWorkspacesQuery query, CancellationToken cancellationToken)
    {
        var rows = await db.Memberships.AsNoTracking()
            .IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(m => m.UserId == caller.UserId)
            .Join(db.Workspaces.Where(w => w.Status != WorkspaceStatus.Deleted), m => m.WorkspaceId, w => w.Id, (m, w) => new { w, m.Role })
            .OrderBy(x => x.w.CreatedAt)
            .ToListAsync(cancellationToken);
        return rows.Select(r => ToResponse(r.w, r.Role)).ToList();
    }

    public async Task<Result<WorkspaceResponse>> HandleAsync(GetWorkspaceQuery query, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == tenant.WorkspaceId, cancellationToken);
        return workspace is null ? Error.NotFound() : ToResponse(workspace, await RoleAsync(cancellationToken));
    }

    public async Task<Result<WorkspaceResponse>> HandleAsync(UpdateWorkspaceCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, cancellationToken);
        var before = new { workspace.Name, workspace.Industry };
        workspace.Update(command.Request.Name, command.Request.Industry);
        db.Publish(new WorkspaceChanged(workspace.Id, "updated"), key: workspace.Id.ToString());
        Audit("workspace.update", "workspace", workspace.Id, before, new { workspace.Name, workspace.Industry });
        cache.Enqueue(db, CacheTags.Workspace(workspace.Id));
        await db.SaveChangesAsync(cancellationToken);
        await cache.InvalidateLocalAsync([CacheTags.Workspace(workspace.Id)], cancellationToken);
        return ToResponse(workspace, await RoleAsync(cancellationToken));
    }

    public async Task<Result<BusinessHoursDto>> HandleAsync(GetBusinessHoursQuery query, CancellationToken cancellationToken)
    {
        var hours = await db.Workspaces.AsNoTracking().Where(w => w.Id == tenant.WorkspaceId).Select(w => w.BusinessHours).FirstAsync(cancellationToken);
        return new BusinessHoursDto(hours.Days, hours.ClosedDates);
    }

    public async Task<Result<BusinessHoursDto>> HandleAsync(SetBusinessHoursCommand command, CancellationToken cancellationToken)
    {
        var workspace = await db.Workspaces.FirstAsync(w => w.Id == tenant.WorkspaceId, cancellationToken);
        workspace.SetBusinessHours(new BusinessHours(command.Hours.Days, command.Hours.ClosedDates));
        db.Publish(new WorkspaceChanged(workspace.Id, "business_hours"), key: workspace.Id.ToString());
        Audit("workspace.business_hours_update", "workspace", workspace.Id, after: command.Hours);
        cache.Enqueue(db, CacheTags.Workspace(workspace.Id));
        await db.SaveChangesAsync(cancellationToken);
        return command.Hours;
    }

    private void Audit(string action, string entityType, Guid entityId, object? before = null, object? after = null)
    {
        var entry = AuditEntries.Create(AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), action, entityType, entityId.ToString(), tenant.WorkspaceId, clock.UtcNow, before, after);
        db.Publish(entry, entry.PartitionKey());
    }

    private async Task<string> RoleAsync(CancellationToken cancellationToken) =>
        await db.Memberships.Where(m => m.UserId == caller.UserId).Select(m => m.Role).FirstOrDefaultAsync(cancellationToken) ?? WorkspaceRoles.Viewer;

    internal static WorkspaceResponse ToResponse(Workspace w, string role) =>
        new(w.Id, w.Name, w.Slug, w.Industry, w.TimeZone, Workspace.StatusName(w.Status), role, w.CreatedAt, w.DeletionScheduledFor);
}
