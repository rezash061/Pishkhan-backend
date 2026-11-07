using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Notifications.Features;

/// <summary>A ticket in the support queue, with its workspace and the staff member handling it.</summary>
public sealed record AdminTicket(
    Guid Id, long Number, Guid WorkspaceId, string? WorkspaceName, string Subject, string Category, string Status, string LastMessageBy, int MessageCount,
    Guid? AssignedTo, string? AssignedToName, Guid CreatedBy, DateTimeOffset LastMessageAt, DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

/// <summary>Staff see every author's real name (staff usernames too).</summary>
public sealed record AdminTicketMessage(Guid Id, string AuthorKind, Guid AuthorId, string? AuthorName, string Body, DateTimeOffset CreatedAt);

public sealed record AdminTicketDetail(AdminTicket Ticket, IReadOnlyList<AdminTicketMessage> Messages);

/// <summary>Counts for the queue tabs. <c>unassigned</c> and <c>mine</c> count tickets not closed.</summary>
public sealed record TicketCounts(int Open, int Answered, int Closed, int Unassigned, int Mine);

/// <summary><c>staffId</c> null takes the ticket off everyone; the caller's own id takes it.</summary>
public sealed record AssignTicketRequest(Guid? StaffId);

/// <summary>
/// An announcement. <c>state</c>: scheduled, active or ended. <c>endsAt</c> null shows it until it is ended or deleted.
/// </summary>
public sealed record AnnouncementResponse(
    Guid Id, string Title, string? Body, string Severity, string? Link, DateTimeOffset StartsAt, DateTimeOffset? EndsAt, string State, Guid CreatedBy,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

/// <summary><c>startsAt</c> null means now.</summary>
public sealed record AnnouncementRequest(string Title, string? Body, string Severity, string? Link, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

/// <summary>Support tickets and announcements in the AdminApi (AD-2.6). Readers: SuperAdmin, Support, ReadOnly; changes: Support.</summary>
internal static class SupportAdminEndpoints
{
    private static readonly string[] Readers = [StaffRoles.SuperAdmin, StaffRoles.Support, StaffRoles.ReadOnly];

    public static void Map(IEndpointRouteBuilder app)
    {
        var api = app.MapAdminApi();
        var read = api.MapGroup(string.Empty).WithTags("Support")
            .RequireAuthorization(p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireClaim(PishkhanClaims.Roles, Readers));
        var write = api.MapGroup(string.Empty).WithTags("Support").RequireAuthorization(AuthPolicies.StaffRole(StaffRoles.Support));

        read.MapGet("/support/tickets", ListAsync)
            .WithSummary("The support queue, latest activity first")
            .WithDescription("`status` open, answered or closed; `category`; `assigned` me or none; `workspaceId`; `q` matches the subject or the " +
                "ticket number.")
            .Produces<PagedResult<AdminTicket>>()
            .WithStandardProblems();

        read.MapGet("/support/tickets/counts", CountsAsync)
            .WithSummary("How many tickets are in each queue tab")
            .Produces<TicketCounts>();

        read.MapGet("/support/tickets/{ticketId:guid}", GetAsync)
            .WithSummary("A ticket and all its messages, oldest first")
            .Produces<AdminTicketDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        write.MapPost("/support/tickets/{ticketId:guid}/messages", ReplyAsync)
            .WithSummary("Answer the seller")
            .WithDescription("Support or SuperAdmin. Marks the ticket answered, takes it if nobody has, and notifies the member who opened it " +
                "(in-app and push). Audited as support.ticket.reply.")
            .Produces<AdminTicketDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        write.MapPost("/support/tickets/{ticketId:guid}/assign", AssignAsync)
            .WithSummary("Give the ticket to a staff member, or to nobody")
            .Produces<AdminTicket>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        write.MapPost("/support/tickets/{ticketId:guid}/close", CloseAsync)
            .WithSummary("Close a ticket")
            .WithDescription("Audited as support.ticket.close.")
            .Produces<AdminTicket>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        write.MapPost("/support/tickets/{ticketId:guid}/reopen", ReopenAsync)
            .WithSummary("Open a closed ticket again")
            .Produces<AdminTicket>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        read.MapGet("/announcements", ListAnnouncementsAsync)
            .WithSummary("Announcements, newest start first")
            .WithDescription("`state` scheduled, active or ended.")
            .Produces<PagedResult<AnnouncementResponse>>();

        write.MapPost("/announcements", CreateAnnouncementAsync)
            .WithSummary("Publish an announcement to every seller's panel")
            .WithDescription("Support or SuperAdmin. Audited as support.announcement.create.")
            .Produces<AnnouncementResponse>(StatusCodes.Status201Created)
            .WithStandardProblems()
            .WithExamples(new AnnouncementRequest("به‌روزرسانی شبانه", "پنل امشب از ساعت ۲ تا ۳ در دسترس نیست.", AnnouncementSeverities.Warning, null, null, null));

        write.MapPut("/announcements/{id:guid}", UpdateAnnouncementAsync)
            .WithSummary("Change an announcement")
            .WithDescription("Audited as support.announcement.update.")
            .Produces<AnnouncementResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        write.MapPost("/announcements/{id:guid}/end", EndAnnouncementAsync)
            .WithSummary("Stop showing an announcement now")
            .WithDescription("Audited as support.announcement.end.")
            .Produces<AnnouncementResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        write.MapDelete("/announcements/{id:guid}", DeleteAnnouncementAsync)
            .WithSummary("Delete an announcement")
            .WithDescription("Audited as support.announcement.delete.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static IQueryable<SupportTicket> AllTickets(NotificationsDbContext db) => db.Tickets.IgnoreQueryFilters([ModuleDbContext.TenantFilter]);

    private static async Task<IReadOnlyList<AdminTicket>> ToAdminAsync(
        IReadOnlyList<SupportTicket> rows, IWorkspaceQueries workspaces, IStaffDirectory staff, CancellationToken ct)
    {
        var names = new Dictionary<Guid, string?>();
        foreach (var ws in rows.Select(r => r.WorkspaceId).Distinct())
        {
            names[ws] = await workspaces.GetNameAsync(ws, ct);
        }

        var staffNames = new Dictionary<Guid, string?>();
        foreach (var id in rows.Where(r => r.AssignedTo != null).Select(r => r.AssignedTo!.Value).Distinct())
        {
            staffNames[id] = await staff.GetUsernameAsync(id, ct);
        }

        return
        [
            .. rows.Select(t => new AdminTicket(
                t.Id, t.Number, t.WorkspaceId, names.GetValueOrDefault(t.WorkspaceId), t.Subject, t.Category, t.Status, t.LastMessageBy, t.MessageCount,
                t.AssignedTo, t.AssignedTo is { } a ? staffNames.GetValueOrDefault(a) : null, t.CreatedBy, t.LastMessageAt, t.CreatedAt, t.ClosedAt)),
        ];
    }

    private static async Task<AdminTicketDetail> DetailAsync(
        NotificationsDbContext db, SupportTicket ticket, IWorkspaceQueries workspaces, IStaffDirectory staff, CancellationToken ct)
    {
        var messages = await db.TicketMessages.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(m => m.TicketId == ticket.Id)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);
        var head = (await ToAdminAsync([ticket], workspaces, staff, ct))[0];
        return new AdminTicketDetail(head, [.. messages.Select(m => new AdminTicketMessage(m.Id, m.AuthorKind, m.AuthorId, m.AuthorName, m.Body, m.CreatedAt))]);
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation("validation.failed", new Dictionary<string, string[]> { [field] = [code] }).ToProblem();

    private static async Task<IResult> ListAsync(
        string? status, string? category, string? assigned, Guid? workspaceId, string? q, int? page, int? pageSize, NotificationsDbContext db,
        ICurrentUser caller, IWorkspaceQueries workspaces, IStaffDirectory staff, CancellationToken ct)
    {
        var query = AllTickets(db).AsNoTracking();
        if (status is { Length: > 0 })
        {
            if (!TicketStatuses.All.Contains(status))
            {
                return Invalid("status", "validation.invalid");
            }

            query = query.Where(t => t.Status == status);
        }

        if (category is { Length: > 0 })
        {
            if (!TicketCategories.All.Contains(category))
            {
                return Invalid("category", "validation.invalid");
            }

            query = query.Where(t => t.Category == category);
        }

        query = assigned switch
        {
            null or "" => query,
            "me" => query.Where(t => t.AssignedTo == caller.UserId),
            "none" => query.Where(t => t.AssignedTo == null),
            _ => null!,
        };
        if (query is null)
        {
            return Invalid("assigned", "validation.invalid");
        }

        if (workspaceId is { } ws)
        {
            query = query.Where(t => t.WorkspaceId == ws);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            query = long.TryParse(PersianDigits.ToLatin(term), out var number)
                ? query.Where(t => t.Number == number || EF.Functions.ILike(t.Subject, "%" + term + "%"))
                : query.Where(t => EF.Functions.ILike(t.Subject, "%" + term + "%"));
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 100));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.LastMessageAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<AdminTicket>(await ToAdminAsync(rows, workspaces, staff, ct), total, p, size));
    }

    private static async Task<IResult> CountsAsync(NotificationsDbContext db, ICurrentUser caller, CancellationToken ct)
    {
        var byStatus = await AllTickets(db).AsNoTracking().GroupBy(t => t.Status).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var notClosed = AllTickets(db).AsNoTracking().Where(t => t.Status != TicketStatuses.Closed);
        return Results.Ok(new TicketCounts(
            byStatus.GetValueOrDefault(TicketStatuses.Open), byStatus.GetValueOrDefault(TicketStatuses.Answered), byStatus.GetValueOrDefault(TicketStatuses.Closed),
            await notClosed.CountAsync(t => t.AssignedTo == null, ct), await notClosed.CountAsync(t => t.AssignedTo == caller.UserId, ct)));
    }

    private static async Task<IResult> GetAsync(Guid ticketId, NotificationsDbContext db, IWorkspaceQueries workspaces, IStaffDirectory staff, CancellationToken ct)
    {
        var ticket = await AllTickets(db).AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        return ticket is null ? Error.NotFound().ToProblem() : Results.Ok(await DetailAsync(db, ticket, workspaces, staff, ct));
    }

    private static async Task<SupportTicket?> LoadAsync(NotificationsDbContext db, Guid ticketId, CancellationToken ct)
    {
        var ticket = await AllTickets(db).FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is not null)
        {
            db.Tenant.SetWorkspace(ticket.WorkspaceId);
        }

        return ticket;
    }

    private static async Task RecordAsync(
        NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, string action, string entityType, string entityId, Guid? workspaceId,
        object? after, CancellationToken ct)
    {
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), caller.IpAddress), action, entityType, entityId, workspaceId, clock.UtcNow,
            after: after);
        db.Publish(entry, entry.PartitionKey(), workspaceId);
    }

    private static async Task<IResult> ReplyAsync(
        Guid ticketId, TicketReplyRequest request, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IWorkspaceQueries workspaces, IClock clock,
        CancellationToken ct)
    {
        if (SupportTicket.ValidateBody(request.Body) is { } error)
        {
            return Invalid("body", error);
        }

        var ticket = await LoadAsync(db, ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var message = ticket.Add(TicketAuthors.Staff, caller.UserId, await staff.GetUsernameAsync(caller.UserId, ct), request.Body, now);
        if (ticket.AssignedTo is null)
        {
            ticket.Assign(caller.UserId, now);
        }

        db.TicketMessages.Add(message);
        var preview = message.Body.Length > 200 ? message.Body[..200] + "…" : message.Body;
        db.Publish(
            new NotificationRequested(ticket.WorkspaceId, ticket.CreatedBy, "support", $"پاسخ پشتیبانی به تیکت «{ticket.Subject}»", preview, $"/support/{ticket.Id}"),
            ticket.WorkspaceId.ToString(), workspaceId: ticket.WorkspaceId);
        await RecordAsync(db, caller, staff, clock, "support.ticket.reply", "support_ticket", ticket.Id.ToString(), ticket.WorkspaceId, new { ticket.Number, messageId = message.Id }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DetailAsync(db, ticket, workspaces, staff, ct));
    }

    private static async Task<IResult> AssignAsync(
        Guid ticketId, AssignTicketRequest request, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IWorkspaceQueries workspaces, IClock clock,
        CancellationToken ct)
    {
        if (request.StaffId is { } staffId && staffId != caller.UserId && await staff.GetUsernameAsync(staffId, ct) is null)
        {
            return Invalid("staffId", "validation.not_found");
        }

        var ticket = await LoadAsync(db, ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound().ToProblem();
        }

        ticket.Assign(request.StaffId, clock.UtcNow);
        await RecordAsync(db, caller, staff, clock, "support.ticket.assign", "support_ticket", ticket.Id.ToString(), ticket.WorkspaceId, new { ticket.Number, request.StaffId }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ToAdminAsync([ticket], workspaces, staff, ct))[0]);
    }

    private static Task<IResult> CloseAsync(
        Guid ticketId, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct) =>
        ChangeAsync(ticketId, db, caller, staff, workspaces, clock, "support.ticket.close", t => t.Close(clock.UtcNow), ct);

    private static Task<IResult> ReopenAsync(
        Guid ticketId, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IWorkspaceQueries workspaces, IClock clock, CancellationToken ct) =>
        ChangeAsync(ticketId, db, caller, staff, workspaces, clock, "support.ticket.reopen", t => t.Reopen(clock.UtcNow), ct);

    private static async Task<IResult> ChangeAsync(
        Guid ticketId, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IWorkspaceQueries workspaces, IClock clock, string action,
        Action<SupportTicket> change, CancellationToken ct)
    {
        var ticket = await LoadAsync(db, ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound().ToProblem();
        }

        change(ticket);
        await RecordAsync(db, caller, staff, clock, action, "support_ticket", ticket.Id.ToString(), ticket.WorkspaceId, new { ticket.Number, ticket.Status }, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok((await ToAdminAsync([ticket], workspaces, staff, ct))[0]);
    }

    private static AnnouncementResponse ToResponse(Announcement a, DateTimeOffset now) =>
        new(a.Id, a.Title, a.Body, a.Severity, a.Link, a.StartsAt, a.EndsAt, a.StateAt(now), a.CreatedBy, a.CreatedAt, a.UpdatedAt);

    private static async Task<IResult> ListAnnouncementsAsync(string? state, int? page, int? pageSize, NotificationsDbContext db, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var query = db.Announcements.AsNoTracking();
        query = state switch
        {
            null or "" => query,
            "scheduled" => query.Where(a => a.StartsAt > now),
            "active" => query.Where(a => a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now)),
            "ended" => query.Where(a => a.EndsAt != null && a.EndsAt <= now),
            _ => null!,
        };
        if (query is null)
        {
            return Invalid("state", "validation.invalid");
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 25, 1, 100));
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(a => a.StartsAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<AnnouncementResponse>([.. rows.Select(a => ToResponse(a, now))], total, p, size));
    }

    private static AnnouncementTerms Terms(AnnouncementRequest r, DateTimeOffset now) =>
        new(r.Title ?? string.Empty, r.Body, r.Severity ?? string.Empty, r.Link, r.StartsAt ?? now, r.EndsAt);

    private static async Task<IResult> CreateAnnouncementAsync(
        AnnouncementRequest request, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var terms = Terms(request, now);
        var errors = Announcement.Validate(terms);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var announcement = Announcement.Create(terms, caller.UserId, now);
        db.Announcements.Add(announcement);
        await RecordAsync(db, caller, staff, clock, "support.announcement.create", "announcement", announcement.Id.ToString(), null, terms, ct);
        await db.SaveChangesAsync(ct);
        return Results.Created($"announcements/{announcement.Id}", ToResponse(announcement, now));
    }

    private static async Task<IResult> UpdateAnnouncementAsync(
        Guid id, AnnouncementRequest request, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        var terms = Terms(request, announcement.StartsAt);
        var errors = Announcement.Validate(terms);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        announcement.Update(terms, now);
        await RecordAsync(db, caller, staff, clock, "support.announcement.update", "announcement", announcement.Id.ToString(), null, terms, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(announcement, now));
    }

    private static async Task<IResult> EndAnnouncementAsync(
        Guid id, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        if (announcement.StateAt(now) != "ended")
        {
            var starts = announcement.StartsAt < now ? announcement.StartsAt : now.AddSeconds(-1);
            announcement.Update(new AnnouncementTerms(announcement.Title, announcement.Body, announcement.Severity, announcement.Link, starts, now), now);
            await RecordAsync(db, caller, staff, clock, "support.announcement.end", "announcement", announcement.Id.ToString(), null, null, ct);
            await db.SaveChangesAsync(ct);
        }

        return Results.Ok(ToResponse(announcement, now));
    }

    private static async Task<IResult> DeleteAnnouncementAsync(
        Guid id, NotificationsDbContext db, ICurrentUser caller, IStaffDirectory staff, IClock clock, CancellationToken ct)
    {
        var announcement = await db.Announcements.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (announcement is null)
        {
            return Error.NotFound().ToProblem();
        }

        db.Announcements.Remove(announcement);
        await RecordAsync(db, caller, staff, clock, "support.announcement.delete", "announcement", announcement.Id.ToString(), null, new { announcement.Title }, ct);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}
