using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Files.Features;

/// <summary>
/// Filters of the quarantine list. <c>status</c>: infected, scan_failed, rejected, or pending (pending_scan for more than 10 minutes).
/// Without <c>status</c> the list holds infected, scan_failed and stuck pending files (admin plan 4.9).
/// </summary>
public sealed record QuarantineSearch(string? Status, Guid? WorkspaceId, string? Purpose, int? Page, int? PageSize);

/// <summary>
/// A file that is not usable. <c>signature</c> is the antivirus signature of an infected file, <c>rejectReason</c> the sanitizer's or the
/// scanner's error. <c>stuck</c> is true for a pending file older than 10 minutes. There is never a download link.
/// </summary>
public sealed record QuarantineFileResponse(
    Guid Id, Guid WorkspaceId, string? WorkspaceName, string Purpose, string Status, string ContentType, long Size, string? OriginalName, string? Signature,
    string? ScanEngineVersion, string? RejectReason, int ScanAttempts, DateTimeOffset CreatedAt, DateTimeOffset? ScannedAt, DateTimeOffset? FalsePositiveAt, bool Stuck);

/// <summary>Counts for the dashboard and the page's tabs: <c>pending</c> counts only stuck files.</summary>
public sealed record QuarantineSummary(int Infected, int ScanFailed, int Pending, int Rejected);

/// <summary>Why the action is taken: 5 to 300 characters, kept in the audit log.</summary>
public sealed record QuarantineActionRequest(string Reason);

/// <summary>AdminApi: the antivirus quarantine (AD-1.6, admin plan 4.9). Never serves the bytes of a file that is not clean.</summary>
internal static class QuarantineAdminEndpoints
{
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(10);
    private const int DefaultPageSize = 25;
    private const int MaxPageSize = 100;
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support];
    private static readonly string[] Filters = ["infected", "scan_failed", "rejected", "pending"];

    public static void Map(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().MapGroup("/files").WithTags("Files");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var support = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support));
        var super = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin));

        read.MapGet("/quarantine", SearchAsync)
            .WithSummary("Files in quarantine, newest first")
            .WithDescription("Roles: SuperAdmin, Support. Infected, scan_failed and pending files older than 10 minutes unless `status` says otherwise.")
            .Produces<PagedResult<QuarantineFileResponse>>()
            .WithStandardProblems();

        read.MapGet("/quarantine/summary", SummaryAsync)
            .WithSummary("How many files sit in each quarantine state")
            .Produces<QuarantineSummary>();

        support.MapPost("/{fileId:guid}/rescan", RescanAsync)
            .WithSummary("Scan the file again")
            .WithDescription("Support or SuperAdmin. Infected, scan_failed and pending files; the file is pending until the scan ends. Audited as file.rescan.")
            .Produces<QuarantineFileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        support.MapPost("/{fileId:guid}/delete", DeleteAsync)
            .RequireStepUp()
            .WithSummary("Delete a quarantined file now")
            .WithDescription("Support or SuperAdmin, step-up. Removes the stored objects and the row; clean files cannot be deleted here. Audited as file.delete.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        super.MapPost("/{fileId:guid}/false-positive", FalsePositiveAsync)
            .RequireStepUp()
            .WithSummary("Mark an infected verdict as a false positive")
            .WithDescription("SuperAdmin, step-up. The file is scanned again without reusing earlier verdicts; it is served only if that scan is clean. Audited as file.false_positive.")
            .Produces<QuarantineFileResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    private static IQueryable<StoredFile> AllFiles(FilesDbContext db) => db.Files.IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static async Task<IResult> SearchAsync([AsParameters] QuarantineSearch search, FilesDbContext db, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var stuckBefore = now - StuckAfter;
        var query = AllFiles(db).AsNoTracking();
        var status = search.Status?.Trim().ToLowerInvariant();
        switch (status)
        {
            case null or "":
                query = query.Where(f => f.Status == FileStatuses.Infected || f.Status == FileStatuses.ScanFailed || (f.Status == FileStatuses.PendingScan && f.CreatedAt < stuckBefore));
                break;
            case "pending":
                query = query.Where(f => f.Status == FileStatuses.PendingScan && f.CreatedAt < stuckBefore);
                break;
            case var s when Filters.Contains(s):
                query = query.Where(f => f.Status == s);
                break;
            default:
                return Invalid("status", "validation.invalid");
        }

        if (search.WorkspaceId is { } workspaceId)
        {
            query = query.Where(f => f.WorkspaceId == workspaceId);
        }

        if (!string.IsNullOrWhiteSpace(search.Purpose))
        {
            query = query.Where(f => f.Purpose == search.Purpose.Trim());
        }

        var page = Math.Max(1, search.Page ?? 1);
        var pageSize = Math.Clamp(search.PageSize ?? DefaultPageSize, 1, MaxPageSize);
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(f => f.CreatedAt).ThenByDescending(f => f.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        var names = new Dictionary<Guid, string?>();
        foreach (var id in rows.Select(f => f.WorkspaceId).Distinct())
        {
            names[id] = await workspaces.GetNameAsync(id, ct);
        }

        return Results.Ok(new PagedResult<QuarantineFileResponse>([.. rows.Select(f => ToResponse(f, names.GetValueOrDefault(f.WorkspaceId), now))], total, page, pageSize));
    }

    private static async Task<IResult> SummaryAsync(FilesDbContext db, IClock clock, CancellationToken ct) =>
        Results.Ok(await CountAsync(db, clock.UtcNow, ct));

    internal static async Task<QuarantineSummary> CountAsync(FilesDbContext db, DateTimeOffset now, CancellationToken ct)
    {
        var stuckBefore = now - StuckAfter;
        var counts = await AllFiles(db).AsNoTracking()
            .Where(f => f.Status == FileStatuses.Infected || f.Status == FileStatuses.ScanFailed || f.Status == FileStatuses.Rejected
                || (f.Status == FileStatuses.PendingScan && f.CreatedAt < stuckBefore))
            .GroupBy(f => f.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Status, x => x.Count, ct);
        return new QuarantineSummary(
            counts.GetValueOrDefault(FileStatuses.Infected), counts.GetValueOrDefault(FileStatuses.ScanFailed), counts.GetValueOrDefault(FileStatuses.PendingScan),
            counts.GetValueOrDefault(FileStatuses.Rejected));
    }

    private static Task<IResult> RescanAsync(Guid fileId, QuarantineActionRequest request, QuarantineActions actions, CancellationToken ct) =>
        actions.RunAsync(fileId, request, "file.rescan", (file, _, _) =>
        {
            if (file.Status is not (FileStatuses.Infected or FileStatuses.ScanFailed or FileStatuses.PendingScan))
            {
                return "files.not_rescannable";
            }

            file.ResetForRescan();
            return null;
        }, ct);

    private static Task<IResult> FalsePositiveAsync(Guid fileId, QuarantineActionRequest request, QuarantineActions actions, CancellationToken ct) =>
        actions.RunAsync(fileId, request, "file.false_positive", (file, staffId, now) =>
        {
            if (file.Status != FileStatuses.Infected)
            {
                return "files.not_infected";
            }

            file.MarkFalsePositive(staffId, now);
            return null;
        }, ct);

    private static Task<IResult> DeleteAsync(Guid fileId, QuarantineActionRequest request, QuarantineActions actions, CancellationToken ct) =>
        actions.DeleteAsync(fileId, request, ct);

    internal static QuarantineFileResponse ToResponse(StoredFile f, string? workspaceName, DateTimeOffset now) =>
        new(f.Id, f.WorkspaceId, workspaceName, f.Purpose, f.Status, f.ContentTypeDetected, f.Size, f.OriginalName, f.Signature, f.ScanEngineVersion, f.RejectReason,
            f.ScanAttempts, f.CreatedAt, f.ScannedAt, f.FalsePositiveAt, f.Status == FileStatuses.PendingScan && f.CreatedAt < now - StuckAfter);

    internal static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();

    internal static string? ReasonError(string? reason) => reason?.Trim().Length switch
    {
        null or < 5 => "validation.too_short",
        > 300 => "validation.too_long",
        _ => null,
    };
}

/// <summary>A staff action on a file in another workspace: change it in that tenant's scope, audit it, request a scan when needed.</summary>
internal sealed class QuarantineActions(FilesDbContext db, IObjectStorage storage, IWorkspaceQueries workspaces, IStaffDirectory staff, ICurrentUser caller, IClock clock)
{
    public async Task<IResult> RunAsync(Guid fileId, QuarantineActionRequest request, string action, Func<StoredFile, Guid, DateTimeOffset, string?> change, CancellationToken ct)
    {
        if (QuarantineAdminEndpoints.ReasonError(request.Reason) is { } reasonError)
        {
            return QuarantineAdminEndpoints.Invalid("reason", reasonError);
        }

        var file = await db.Files.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var before = new { file.Status, file.Signature };
        if (change(file, caller.UserId, now) is { } conflict)
        {
            return Error.Conflict(conflict).ToProblem();
        }

        db.Tenant.SetWorkspace(file.WorkspaceId);
        db.Publish(new FileScanRequested(file.Id, file.WorkspaceId), key: file.Id.ToString(), workspaceId: file.WorkspaceId);
        await AuditAsync(file, action, before, new { file.Status }, request.Reason, now, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(QuarantineAdminEndpoints.ToResponse(file, await workspaces.GetNameAsync(file.WorkspaceId, ct), now));
    }

    public async Task<IResult> DeleteAsync(Guid fileId, QuarantineActionRequest request, CancellationToken ct)
    {
        if (QuarantineAdminEndpoints.ReasonError(request.Reason) is { } reasonError)
        {
            return QuarantineAdminEndpoints.Invalid("reason", reasonError);
        }

        var file = await db.Files.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(f => f.Id == fileId, ct);
        if (file is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (file.Status == FileStatuses.Clean)
        {
            return Error.Conflict("files.clean").ToProblem();
        }

        // Objects first: a crash leaves the row, so the delete can be retried.
        await storage.DeleteAsync(Buckets.Quarantine, file.StorageKey, ct);
        await storage.DeleteAsync(Buckets.Files, file.StorageKey, ct);
        if (file.Derived.ThumbnailKey is { } thumbnail)
        {
            await storage.DeleteAsync(Buckets.Files, thumbnail, ct);
        }

        db.Tenant.SetWorkspace(file.WorkspaceId);
        db.Files.Remove(file);
        await AuditAsync(file, "file.delete", new { file.Status, file.Signature, file.Purpose, file.Size, file.Sha256 }, null, request.Reason, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private async Task AuditAsync(StoredFile file, string action, object before, object? after, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, "file", file.Id.ToString(), file.WorkspaceId, now,
            before, after, reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: file.WorkspaceId);
    }
}
