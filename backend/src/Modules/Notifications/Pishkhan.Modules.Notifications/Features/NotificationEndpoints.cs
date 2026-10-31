using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Notifications.Features;

public sealed record NotificationResponse(Guid Id, string Kind, string Title, string? Body, string? Link, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

/// <summary>The caller's notifications in this workspace, newest first, and how many are unread.</summary>
public sealed record NotificationsPage(IReadOnlyList<NotificationResponse> Items, string? NextCursor, int UnreadCount);

/// <summary>
/// One kind and the channels on for the caller. <c>smsAvailable</c> false means the kind never sends SMS. <c>push</c> is stored
/// and used once web push is available.
/// </summary>
public sealed record PreferenceResponse(string Kind, string Label, bool InApp, bool Push, bool Sms, bool SmsAvailable);

public sealed record PreferenceItem(string Kind, bool InApp, bool Push, bool Sms);

/// <summary>Kinds left out keep their current setting.</summary>
public sealed record SetPreferencesRequest(IReadOnlyList<PreferenceItem> Items);

internal static class NotificationEndpoints
{
    private const int MaxPageSize = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Notifications");

        ws.MapGet("/notifications", ListAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The caller's notifications (newest first) and the unread count")
            .WithDescription("`unread=true` lists only unread ones. Kinds: `handoff`, `automation`, `channel`; others may appear later.")
            .Produces<NotificationsPage>()
            .WithStandardProblems();

        ws.MapPost("/notifications/{notificationId:guid}/read", MarkReadAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Mark one notification read")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/notifications/read-all", MarkAllReadAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Mark all the caller's notifications in this workspace read")
            .Produces(StatusCodes.Status204NoContent);

        ws.MapGet("/notification-preferences", GetPreferencesAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The caller's notification settings, one entry per kind")
            .Produces<IReadOnlyList<PreferenceResponse>>();

        ws.MapPut("/notification-preferences", SetPreferencesAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Change the caller's notification settings")
            .WithDescription("SMS can be turned on only for kinds with `smsAvailable`; at most one SMS per kind every 30 minutes is sent.")
            .Produces<IReadOnlyList<PreferenceResponse>>()
            .WithStandardProblems()
            .WithExamples(new SetPreferencesRequest([new PreferenceItem("handoff", true, true, true), new PreferenceItem("automation", true, false, false)]));
    }

    private static async Task<IResult> ListAsync(bool? unread, string? cursor, int? limit, NotificationsDbContext db, ICurrentUser caller, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 30, 1, MaxPageSize);
        var mine = db.Notifications.AsNoTracking().Where(n => n.UserId == caller.UserId);
        var query = unread == true ? mine.Where(n => n.ReadAt == null) : mine;
        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(n => EF.Functions.LessThan(ValueTuple.Create(n.CreatedAt, n.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        var unreadCount = await mine.CountAsync(n => n.ReadAt == null, ct);
        return Results.Ok(new NotificationsPage([.. rows.Take(take).Select(ToResponse)], next, unreadCount));
    }

    private static async Task<IResult> MarkReadAsync(Guid notificationId, NotificationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var notification = await db.Notifications.FirstOrDefaultAsync(n => n.Id == notificationId && n.UserId == caller.UserId, ct);
        if (notification is null)
        {
            return Error.NotFound().ToProblem();
        }

        notification.MarkRead(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    private static async Task<IResult> MarkAllReadAsync(NotificationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        await db.Notifications.Where(n => n.UserId == caller.UserId && n.ReadAt == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct);
        return Results.NoContent();
    }

    private static async Task<IResult> GetPreferencesAsync(NotificationsDbContext db, ICurrentUser caller, CancellationToken ct) =>
        Results.Ok(await PreferencesAsync(db, caller.UserId, ct));

    private static async Task<IResult> SetPreferencesAsync(
        SetPreferencesRequest request, NotificationsDbContext db, ITenantContext tenant, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var items = request.Items ?? [];
        for (var i = 0; i < items.Count; i++)
        {
            var kind = NotificationCatalog.Kinds.FirstOrDefault(k => k.Kind == items[i].Kind);
            if (kind is null)
            {
                errors[$"items[{i}].kind"] = ["notifications.kind_unknown"];
            }
            else if (items[i].Sms && !kind.SmsAvailable)
            {
                errors[$"items[{i}].sms"] = ["notifications.sms_unavailable"];
            }
        }

        if (items.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count() != items.Count)
        {
            errors["items"] = ["validation.duplicate"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var existing = await db.Preferences.Where(p => p.UserId == caller.UserId).ToDictionaryAsync(p => p.Kind, StringComparer.Ordinal, ct);
        var now = clock.UtcNow;
        foreach (var item in items)
        {
            if (!existing.TryGetValue(item.Kind, out var preference))
            {
                preference = NotificationPreference.Create(tenant.WorkspaceId!.Value, caller.UserId, item.Kind);
                db.Preferences.Add(preference);
            }

            preference.Set(item.InApp, item.Push, item.Sms, now);
        }

        await db.SaveChangesAsync(ct);
        return Results.Ok(await PreferencesAsync(db, caller.UserId, ct));
    }

    private static async Task<IReadOnlyList<PreferenceResponse>> PreferencesAsync(NotificationsDbContext db, Guid userId, CancellationToken ct)
    {
        var rows = await db.Preferences.AsNoTracking().Where(p => p.UserId == userId).ToDictionaryAsync(p => p.Kind, StringComparer.Ordinal, ct);
        return
        [
            .. NotificationCatalog.Kinds.Select(k =>
            {
                var choice = NotificationCatalog.Effective(k, rows.GetValueOrDefault(k.Kind));
                return new PreferenceResponse(k.Kind, k.Label, choice.InApp, choice.Push, choice.Sms, k.SmsAvailable);
            }),
        ];
    }

    private static NotificationResponse ToResponse(Notification n) => new(n.Id, n.Kind, n.Title, n.Body, n.Link, n.CreatedAt, n.ReadAt);
}
