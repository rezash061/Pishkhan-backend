using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Domain;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Backoffice.Features;

/// <summary>
/// A global flag. <c>rolloutPercent</c> (0 to 100) turns it on for a stable share of workspaces; <c>plans</c> (plan codes) limits it to
/// workspaces on those plans, empty for every plan. <c>overrides</c> counts per-workspace overrides.
/// </summary>
public sealed record FeatureFlagResponse(
    string Key, string? Description, bool Enabled, int RolloutPercent, IReadOnlyList<string> Plans, int Overrides, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    string? UpdatedBy);

/// <summary>Creates or changes a flag. <c>key</c> (create only): lower case letters, digits, '.', '_' and '-', 2 to 60 characters.</summary>
public sealed record FeatureFlagRequest(string? Key, string? Description, bool Enabled, int RolloutPercent, IReadOnlyList<string>? Plans, string Reason);

public sealed record FeatureFlagDeleteRequest(string Reason);

/// <summary>
/// A flag as it applies to one workspace. <c>override</c> is the workspace's override (null: none); <c>effective</c> what the workspace
/// gets; <c>source</c>: override, rollout, plan (not on the workspace's plan), off (flag disabled) or rollout_excluded.
/// </summary>
public sealed record WorkspaceFeatureFlag(string Key, string? Description, bool Effective, string Source, bool? Override, string? OverrideReason, DateTimeOffset? OverriddenAt, string? OverriddenBy);

/// <summary><c>enabled</c>: true or false to override, null to remove the override. <c>reason</c> 5 to 300 characters.</summary>
public sealed record WorkspaceFlagOverrideRequest(bool? Enabled, string Reason);

/// <summary>The panel's view: the keys of the flags that are on for the workspace.</summary>
public sealed record WorkspaceFeaturesResponse(IReadOnlyList<string> Enabled);

/// <summary>Evaluates flags with a 30-second in-process cache of the flag table (changes reach every host within 30 seconds).</summary>
internal sealed class FeatureFlagEvaluator(BackofficeDbContext db, IServiceProvider services) : IFeatureFlags
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static (DateTimeOffset At, IReadOnlyList<FeatureFlag> Flags, ILookup<Guid, FeatureFlagOverride> Overrides)? _cache;

    public async Task<bool> IsEnabledAsync(string key, Guid workspaceId, CancellationToken cancellationToken) =>
        (await GetEnabledAsync(workspaceId, cancellationToken)).Contains(key, StringComparer.Ordinal);

    public async Task<IReadOnlyList<string>> GetEnabledAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var (flags, overrides) = await LoadAsync(cancellationToken);
        if (flags.Count == 0)
        {
            return [];
        }

        var plan = await PlanCodeAsync(workspaceId, cancellationToken);
        var own = overrides[workspaceId].ToDictionary(o => o.Key, StringComparer.Ordinal);
        return [.. flags.Where(f => Evaluate(f, workspaceId, plan, own.GetValueOrDefault(f.Key)?.Enabled).Effective).Select(f => f.Key)];
    }

    public static void Invalidate() => _cache = null;

    private async Task<(IReadOnlyList<FeatureFlag>, ILookup<Guid, FeatureFlagOverride>)> LoadAsync(CancellationToken ct)
    {
        if (_cache is { } hit && DateTimeOffset.UtcNow - hit.At < CacheFor)
        {
            return (hit.Flags, hit.Overrides);
        }

        var flags = await db.FeatureFlags.AsNoTracking().ToListAsync(ct);
        var overrides = (await db.FeatureFlagOverrides.AsNoTracking().ToListAsync(ct)).ToLookup(o => o.WorkspaceId);
        _cache = (DateTimeOffset.UtcNow, flags, overrides);
        return (flags, overrides);
    }

    internal async Task<string?> PlanCodeAsync(Guid workspaceId, CancellationToken ct) =>
        services.GetService<IEntitlements>() is { } entitlements ? (await entitlements.GetAsync(workspaceId, ct)).PlanCode : null;

    /// <summary>Override first, then the flag's switch, plan list and rollout share.</summary>
    internal static (bool Effective, string Source) Evaluate(FeatureFlag flag, Guid workspaceId, string? planCode, bool? overridden)
    {
        if (overridden is { } value)
        {
            return (value, "override");
        }

        if (!flag.Enabled)
        {
            return (false, "off");
        }

        if (flag.Plans.Count > 0 && (planCode is null || !flag.Plans.Contains(planCode, StringComparer.Ordinal)))
        {
            return (false, "plan");
        }

        return Bucket(flag.Key, workspaceId) < flag.RolloutPercent ? (true, "rollout") : (false, "rollout_excluded");
    }

    /// <summary>A stable 0..99 bucket per (flag, workspace), so raising the percentage only adds workspaces.</summary>
    internal static int Bucket(string key, Guid workspaceId)
    {
        var hash = 2166136261u;
        foreach (var c in key + ":" + workspaceId.ToString("N"))
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return (int)(hash % 100);
    }
}

/// <summary>Feature flags (AD-1.8, admin plan 4.10): global flags with rollout and plan targeting, per-workspace overrides.</summary>
internal static partial class FeatureFlagEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.AiOps, StaffRoles.Finance, StaffRoles.ReadOnly];

    [GeneratedRegex("^[a-z][a-z0-9._-]{1,59}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Feature flags");
        var read = admin.MapGroup(string.Empty).RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var super = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.SuperAdmin));
        var support = admin.MapGroup(string.Empty).RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support));

        read.MapGet("/feature-flags", ListAsync)
            .WithSummary("Feature flags").WithDescription("Roles: SuperAdmin, Support, AIOps, Finance, ReadOnly.")
            .Produces<IReadOnlyList<FeatureFlagResponse>>();
        super.MapPost("/feature-flags", CreateAsync)
            .WithSummary("Create a flag").WithDescription("SuperAdmin. 409 `backoffice.flag_exists`. Audited as feature_flag.create.")
            .Produces<FeatureFlagResponse>(StatusCodes.Status201Created).ProducesProblem(StatusCodes.Status409Conflict).WithStandardProblems();
        super.MapPut("/feature-flags/{key}", UpdateAsync)
            .WithSummary("Change a flag").WithDescription("SuperAdmin. Reaches every host within 30 seconds. Audited as feature_flag.update.")
            .Produces<FeatureFlagResponse>().ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();
        super.MapPost("/feature-flags/{key}/delete", DeleteAsync)
            .RequireStepUp()
            .WithSummary("Delete a flag and its overrides").WithDescription("SuperAdmin, step-up. Audited as feature_flag.delete.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();

        read.MapGet("/workspaces/{workspaceId:guid}/feature-flags", WorkspaceFlagsAsync)
            .WithSummary("Every flag as it applies to a workspace, with its override")
            .Produces<IReadOnlyList<WorkspaceFeatureFlag>>();
        support.MapPut("/workspaces/{workspaceId:guid}/feature-flags/{key}", OverrideAsync)
            .WithSummary("Turn a flag on or off for one workspace, or remove the override")
            .WithDescription("Support or SuperAdmin. Audited as feature_flag.override in the workspace's log.")
            .Produces<WorkspaceFeatureFlag>().ProducesProblem(StatusCodes.Status404NotFound).WithStandardProblems();
    }

    public static void MapPanel(IEndpointRouteBuilder app) =>
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Workspaces")
            .MapGet("/features", async (Guid workspaceId, IFeatureFlags flags, CancellationToken ct) => Results.Ok(new WorkspaceFeaturesResponse(await flags.GetEnabledAsync(workspaceId, ct))))
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Feature flags that are on for this workspace")
            .Produces<WorkspaceFeaturesResponse>();

    private static async Task<IResult> ListAsync(BackofficeDbContext db, CancellationToken ct)
    {
        var flags = await db.FeatureFlags.AsNoTracking().OrderBy(f => f.Key).ToListAsync(ct);
        var counts = await db.FeatureFlagOverrides.AsNoTracking().GroupBy(o => o.Key).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var names = await NamesAsync(db, flags.Select(f => f.UpdatedBy), ct);
        return Results.Ok(flags.Select(f => ToResponse(f, counts.GetValueOrDefault(f.Key), names)).ToList());
    }

    private static async Task<IResult> CreateAsync(FeatureFlagRequest request, BackofficeDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var key = request.Key?.Trim() ?? string.Empty;
        var errors = Validate(request);
        if (!KeyPattern().IsMatch(key))
        {
            errors["key"] = ["validation.invalid"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.FeatureFlags.AnyAsync(f => f.Key == key, ct))
        {
            return Error.Conflict("backoffice.flag_exists").ToProblem();
        }

        var now = clock.UtcNow;
        var flag = FeatureFlag.Create(key, Clean(request.Description), request.Enabled, request.RolloutPercent, CleanPlans(request.Plans), caller.UserId, now);
        db.FeatureFlags.Add(flag);
        await AuditAsync(db, caller, "feature_flag.create", key, null, null, Snapshot(flag), request.Reason, now, ct);
        await db.SaveChangesAsync(ct);
        FeatureFlagEvaluator.Invalidate();
        var names = await NamesAsync(db, [flag.UpdatedBy], ct);
        return Results.Created($"/admin/v1/feature-flags/{key}", ToResponse(flag, 0, names));
    }

    private static async Task<IResult> UpdateAsync(string key, FeatureFlagRequest request, BackofficeDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var errors = Validate(request);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var before = Snapshot(flag);
        flag.Change(Clean(request.Description), request.Enabled, request.RolloutPercent, CleanPlans(request.Plans), caller.UserId, now);
        await AuditAsync(db, caller, "feature_flag.update", key, null, before, Snapshot(flag), request.Reason, now, ct);
        await db.SaveChangesAsync(ct);
        FeatureFlagEvaluator.Invalidate();
        var count = await db.FeatureFlagOverrides.CountAsync(o => o.Key == key, ct);
        return Results.Ok(ToResponse(flag, count, await NamesAsync(db, [flag.UpdatedBy], ct)));
    }

    private static async Task<IResult> DeleteAsync(string key, FeatureFlagDeleteRequest request, BackofficeDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (PlatformOpsEndpoints.ReasonError(request.Reason) is { } reasonError)
        {
            return PlatformOpsEndpoints.Invalid("reason", reasonError);
        }

        var flag = await db.FeatureFlags.FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null)
        {
            return Error.NotFound().ToProblem();
        }

        var overrides = await db.FeatureFlagOverrides.Where(o => o.Key == key).ToListAsync(ct);
        db.FeatureFlagOverrides.RemoveRange(overrides);
        db.FeatureFlags.Remove(flag);
        await AuditAsync(db, caller, "feature_flag.delete", key, null, new { flag = Snapshot(flag), overrides = overrides.Count }, null, request.Reason, clock.UtcNow, ct);
        await db.SaveChangesAsync(ct);
        FeatureFlagEvaluator.Invalidate();
        return Results.NoContent();
    }

    private static async Task<IResult> WorkspaceFlagsAsync(Guid workspaceId, BackofficeDbContext db, FeatureFlagEvaluator evaluator, CancellationToken ct)
    {
        var flags = await db.FeatureFlags.AsNoTracking().OrderBy(f => f.Key).ToListAsync(ct);
        var overrides = await db.FeatureFlagOverrides.AsNoTracking().Where(o => o.WorkspaceId == workspaceId).ToDictionaryAsync(o => o.Key, StringComparer.Ordinal, ct);
        var plan = await evaluator.PlanCodeAsync(workspaceId, ct);
        var names = await NamesAsync(db, overrides.Values.Select(o => (Guid?)o.UpdatedBy), ct);
        return Results.Ok(flags.Select(f => ToWorkspace(f, workspaceId, plan, overrides.GetValueOrDefault(f.Key), names)).ToList());
    }

    private static async Task<IResult> OverrideAsync(
        Guid workspaceId, string key, WorkspaceFlagOverrideRequest request, BackofficeDbContext db, FeatureFlagEvaluator evaluator, IWorkspaceQueries workspaces, ICurrentUser caller,
        IClock clock, CancellationToken ct)
    {
        if (PlatformOpsEndpoints.ReasonError(request.Reason) is { } reasonError)
        {
            return PlatformOpsEndpoints.Invalid("reason", reasonError);
        }

        var flag = await db.FeatureFlags.AsNoTracking().FirstOrDefaultAsync(f => f.Key == key, ct);
        if (flag is null || await workspaces.GetNameAsync(workspaceId, ct) is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var row = await db.FeatureFlagOverrides.FirstOrDefaultAsync(o => o.Key == key && o.WorkspaceId == workspaceId, ct);
        var before = row?.Enabled;
        if (request.Enabled is { } enabled)
        {
            if (row is null)
            {
                row = new FeatureFlagOverride { Key = key, WorkspaceId = workspaceId };
                db.FeatureFlagOverrides.Add(row);
            }

            row.Enabled = enabled;
            row.Reason = request.Reason.Trim();
            row.UpdatedBy = caller.UserId;
            row.UpdatedAt = now;
        }
        else if (row is not null)
        {
            db.FeatureFlagOverrides.Remove(row);
            row = null;
        }

        await AuditAsync(db, caller, "feature_flag.override", key, workspaceId, new { @override = before }, new { @override = request.Enabled }, request.Reason, now, ct);
        await db.SaveChangesAsync(ct);
        FeatureFlagEvaluator.Invalidate();
        var plan = await evaluator.PlanCodeAsync(workspaceId, ct);
        return Results.Ok(ToWorkspace(flag, workspaceId, plan, row, await NamesAsync(db, [row?.UpdatedBy], ct)));
    }

    private static WorkspaceFeatureFlag ToWorkspace(FeatureFlag f, Guid workspaceId, string? plan, FeatureFlagOverride? o, Dictionary<Guid, string?> names)
    {
        var (effective, source) = FeatureFlagEvaluator.Evaluate(f, workspaceId, plan, o?.Enabled);
        return new WorkspaceFeatureFlag(f.Key, f.Description, effective, source, o?.Enabled, o?.Reason, o?.UpdatedAt, o is null ? null : names.GetValueOrDefault(o.UpdatedBy));
    }

    private static Dictionary<string, string[]> Validate(FeatureFlagRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (PlatformOpsEndpoints.ReasonError(request.Reason) is { } reasonError)
        {
            errors["reason"] = [reasonError];
        }

        if (request.RolloutPercent is < 0 or > 100)
        {
            errors["rolloutPercent"] = ["validation.out_of_range"];
        }

        if (request.Description is { Length: > 300 })
        {
            errors["description"] = ["validation.too_long"];
        }

        if (request.Plans is { Count: > 20 } || request.Plans?.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 40) == true)
        {
            errors["plans"] = ["validation.invalid"];
        }

        return errors;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> CleanPlans(IReadOnlyList<string>? plans) => [.. (plans ?? []).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.Ordinal)];

    private static object Snapshot(FeatureFlag f) => new { f.Enabled, f.RolloutPercent, f.Plans, f.Description };

    private static FeatureFlagResponse ToResponse(FeatureFlag f, int overrides, Dictionary<Guid, string?> names) =>
        new(f.Key, f.Description, f.Enabled, f.RolloutPercent, f.Plans, overrides, f.CreatedAt, f.UpdatedAt, f.UpdatedBy is { } by ? names.GetValueOrDefault(by) : null);

    private static async Task<Dictionary<Guid, string?>> NamesAsync(BackofficeDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.StaffUsers.AsNoTracking().Where(s => wanted.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => (string?)s.Username, ct);
    }

    private static async Task AuditAsync(
        BackofficeDbContext db, ICurrentUser caller, string action, string key, Guid? workspaceId, object? before, object? after, string reason, DateTimeOffset now, CancellationToken ct)
    {
        var username = await db.StaffUsers.AsNoTracking().Where(s => s.Id == caller.UserId).Select(s => s.Username).FirstOrDefaultAsync(ct);
        var entry = AuditEntries.Create(AuditActor.Staff(caller.UserId, username, caller.IpAddress), action, "feature_flag", key, workspaceId, now, before, after, reason.Trim());
        db.Publish(entry, entry.PartitionKey(), workspaceId: workspaceId);
    }
}
