using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Channels.Features;

/// <summary><c>status</c>: up or down. <c>mode</c>: auto (the detector decides), forced_down or forced_up (set by staff).</summary>
public sealed record OutageStatusResponse(string Kind, string Status, string Mode, DateTimeOffset Since);

/// <summary>Back office: the detector's counts as well (<c>ok</c> and <c>failed</c> Graph API calls of the window).</summary>
public sealed record AdminOutageResponse(string Kind, string Status, string Mode, DateTimeOffset Since, string? ModeReason, long Ok, long Failed, int WindowMinutes);

/// <summary><c>mode</c>: auto, forced_down or forced_up. <c>reason</c> (5 to 300 characters) is kept in the audit log.</summary>
public sealed record OutageModeRequest(string Mode, string Reason);

/// <summary>The Instagram outage status (BE-2.7): the panel's banner, and the back office's manual "outage mode".</summary>
internal static class OutageEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.ReadOnly, StaffRoles.AiOps, StaffRoles.Finance];

    public static void MapPanel(IEndpointRouteBuilder app) =>
        app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/channels").WithTags("Channels")
            .MapGet("/outage", PanelAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Whether Instagram is working, for the outage banner")
            .Produces<OutageStatusResponse>();

    public static void MapAdmin(IEndpointRouteBuilder app)
    {
        var admin = app.MapAdminApi().WithTags("Platform");
        admin.MapGet("/platform/outage", AdminGetAsync)
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers))
            .WithSummary("The Instagram outage status and the detector's counts")
            .Produces<AdminOutageResponse>();

        admin.MapPut("/platform/outage", AdminSetAsync)
            .RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support))
            .RequireStepUp()
            .WithSummary("Set the outage mode: let the detector decide, or declare Instagram down or up")
            .Produces<AdminOutageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);
    }

    private static async Task<IResult> PanelAsync(Guid workspaceId, IChannelHealth health, CancellationToken ct)
    {
        var s = await health.GetInstagramAsync(ct);
        return Results.Ok(new OutageStatusResponse(s.Kind, s.Status, s.Mode, s.Since));
    }

    private static async Task<IResult> AdminGetAsync(ChannelsDbContext db, InstagramHealth health, Microsoft.Extensions.Options.IOptions<OutageOptions> options, IClock clock, CancellationToken ct)
    {
        var row = await db.PlatformStatuses.AsNoTracking().FirstOrDefaultAsync(s => s.Kind == ChannelKinds.Instagram, ct) ?? PlatformChannelStatus.New(ChannelKinds.Instagram, clock.UtcNow);
        var counts = await health.CountAsync(options.Value.WindowMinutes);
        return Results.Ok(ToAdmin(row, counts, options.Value));
    }

    private static async Task<IResult> AdminSetAsync(
        OutageModeRequest request, ChannelsDbContext db, InstagramHealth health, Microsoft.Extensions.Options.IOptions<OutageOptions> options, ICurrentUser caller, IClock clock,
        CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!OutageModes.All.Contains(request.Mode ?? string.Empty))
        {
            errors["mode"] = ["validation.invalid"];
        }

        if (request.Reason?.Trim().Length is not (>= 5 and <= 300))
        {
            errors["reason"] = [(request.Reason?.Trim().Length ?? 0) < 5 ? "validation.too_short" : "validation.too_long"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var now = clock.UtcNow;
        var row = await db.PlatformStatuses.FirstOrDefaultAsync(s => s.Kind == ChannelKinds.Instagram, ct);
        if (row is null)
        {
            row = PlatformChannelStatus.New(ChannelKinds.Instagram, now);
            db.PlatformStatuses.Add(row);
        }

        var before = new { row.Status, row.Mode };
        row.SetMode(request.Mode!, request.Reason!.Trim(), caller.UserId, now);
        var counts = await health.CountAsync(options.Value.WindowMinutes);
        if (row.Evaluate(counts, options.Value, now))
        {
            await InstagramOutageJob.AnnounceAsync(db, row, ct);
        }

        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, null, caller.IpAddress), "platform.outage_mode_set", "platform_status", row.Kind, null, now,
            before, new { row.Status, row.Mode }, row.ModeReason);
        db.Publish(entry, entry.PartitionKey(), workspaceId: null);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToAdmin(row, counts, options.Value));
    }

    private static AdminOutageResponse ToAdmin(PlatformChannelStatus s, CallCounts counts, OutageOptions options) =>
        new(s.Kind, s.Status, s.Mode, s.Since, s.ModeReason, counts.Ok, counts.Failed, options.WindowMinutes);
}
