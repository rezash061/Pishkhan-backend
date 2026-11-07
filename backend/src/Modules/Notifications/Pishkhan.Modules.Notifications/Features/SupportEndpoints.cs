using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Notifications.Features;

/// <summary>
/// A support ticket. <c>category</c>: billing, technical, instagram, ai, other. <c>status</c>: open (waiting on Pishkhan), answered
/// (waiting on the seller) or closed. <c>lastMessageBy</c>: seller or staff.
/// </summary>
public sealed record TicketResponse(
    Guid Id, long Number, string Subject, string Category, string Status, string LastMessageBy, int MessageCount, DateTimeOffset LastMessageAt,
    DateTimeOffset CreatedAt, DateTimeOffset? ClosedAt);

/// <summary>One message. <c>authorKind</c> seller or staff; staff messages carry «پشتیبانی پیشخوان» as the name.</summary>
public sealed record TicketMessageResponse(Guid Id, string AuthorKind, Guid? AuthorId, string? AuthorName, string Body, DateTimeOffset CreatedAt);

public sealed record TicketDetail(TicketResponse Ticket, IReadOnlyList<TicketMessageResponse> Messages);

/// <summary>Opens a ticket. <c>subject</c> 3 to 150 characters, <c>body</c> up to 4,000.</summary>
public sealed record OpenTicketRequest(string Subject, string Category, string Body);

public sealed record TicketReplyRequest(string Body);

/// <summary>A platform notice for the banner. <c>severity</c> info, warning or critical; <c>link</c> a panel path or https URL.</summary>
public sealed record ActiveAnnouncement(Guid Id, string Title, string? Body, string Severity, string? Link, DateTimeOffset StartsAt, DateTimeOffset? EndsAt);

/// <summary>Seller side of support (UP-1.18): tickets with Pishkhan's support team, and the platform announcements banner.</summary>
internal static class SupportEndpoints
{
    public const string StaffName = "پشتیبانی پیشخوان";

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Support");

        ws.MapGet("/support/tickets", ListAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("The workspace's support tickets, latest activity first")
            .WithDescription("Any member. `status` open, answered or closed filters them.")
            .Produces<PagedResult<TicketResponse>>()
            .WithStandardProblems();

        ws.MapPost("/support/tickets", OpenAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Open a support ticket")
            .WithDescription("409 `support.ticket.too_many_open` when the workspace already has 10 tickets not closed.")
            .Produces<TicketDetail>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems()
            .WithExamples(new OpenTicketRequest("پرداخت اشتراک انجام نشد", TicketCategories.Billing, "مبلغ از حسابم کم شد ولی اشتراک فعال نشد."));

        ws.MapGet("/support/tickets/{ticketId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("A ticket and all its messages, oldest first")
            .Produces<TicketDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/support/tickets/{ticketId:guid}/messages", ReplyAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Add a message; a closed ticket opens again")
            .Produces<TicketDetail>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        ws.MapPost("/support/tickets/{ticketId:guid}/close", CloseAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Close a ticket (the problem is solved)")
            .Produces<TicketResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet("/announcements", AnnouncementsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Platform announcements showing now, most severe first")
            .Produces<IReadOnlyList<ActiveAnnouncement>>();
    }

    internal static TicketResponse ToResponse(SupportTicket t) =>
        new(t.Id, t.Number, t.Subject, t.Category, t.Status, t.LastMessageBy, t.MessageCount, t.LastMessageAt, t.CreatedAt, t.ClosedAt);

    private static TicketMessageResponse ForSeller(TicketMessage m) => m.AuthorKind == TicketAuthors.Staff
        ? new(m.Id, m.AuthorKind, null, StaffName, m.Body, m.CreatedAt)
        : new(m.Id, m.AuthorKind, m.AuthorId, m.AuthorName, m.Body, m.CreatedAt);

    private static async Task<TicketDetail> DetailAsync(NotificationsDbContext db, SupportTicket ticket, CancellationToken ct)
    {
        var messages = await db.TicketMessages.AsNoTracking().Where(m => m.TicketId == ticket.Id).OrderBy(m => m.CreatedAt).ThenBy(m => m.Id).ToListAsync(ct);
        return new TicketDetail(ToResponse(ticket), [.. messages.Select(ForSeller)]);
    }

    private static async Task<IResult> ListAsync(string? status, int? page, int? pageSize, NotificationsDbContext db, CancellationToken ct)
    {
        if (status is { Length: > 0 } && !TicketStatuses.All.Contains(status))
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["status"] = ["validation.invalid"] }).ToProblem();
        }

        var (p, size) = (Math.Max(1, page ?? 1), Math.Clamp(pageSize ?? 20, 1, 100));
        var query = db.Tickets.AsNoTracking();
        if (status is { Length: > 0 })
        {
            query = query.Where(t => t.Status == status);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(t => t.LastMessageAt).Skip((p - 1) * size).Take(size).ToListAsync(ct);
        return Results.Ok(new PagedResult<TicketResponse>([.. rows.Select(ToResponse)], total, p, size));
    }

    private static async Task<IResult> OpenAsync(
        OpenTicketRequest request, NotificationsDbContext db, ITenantContext tenant, ICurrentUser caller, IUserDirectory users, IClock clock, CancellationToken ct)
    {
        var errors = SupportTicket.Validate(request.Subject, request.Category, request.Body);
        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        if (await db.Tickets.CountAsync(t => t.Status != TicketStatuses.Closed, ct) >= SupportTicket.MaxOpenPerWorkspace)
        {
            return Error.Conflict("support.ticket.too_many_open").ToProblem();
        }

        var number = await db.Database.SqlQueryRaw<long>(NotificationsDbContext.NextTicketSql).SingleAsync(ct);
        var name = (await users.GetAsync(caller.UserId, ct))?.DisplayName;
        var (ticket, first) = SupportTicket.Open(tenant.WorkspaceId!.Value, number, request.Subject, request.Category, request.Body, caller.UserId, name, clock.UtcNow);
        db.Tickets.Add(ticket);
        db.TicketMessages.Add(first);
        await db.SaveChangesAsync(ct);
        return Results.Created($"support/tickets/{ticket.Id}", new TicketDetail(ToResponse(ticket), [ForSeller(first)]));
    }

    private static async Task<IResult> GetAsync(Guid ticketId, NotificationsDbContext db, CancellationToken ct)
    {
        var ticket = await db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        return ticket is null ? Error.NotFound().ToProblem() : Results.Ok(await DetailAsync(db, ticket, ct));
    }

    private static async Task<IResult> ReplyAsync(
        Guid ticketId, TicketReplyRequest request, NotificationsDbContext db, ICurrentUser caller, IUserDirectory users, IClock clock, CancellationToken ct)
    {
        if (SupportTicket.ValidateBody(request.Body) is { } error)
        {
            return Error.Validation("validation.failed", new Dictionary<string, string[]> { ["body"] = [error] }).ToProblem();
        }

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound().ToProblem();
        }

        var name = (await users.GetAsync(caller.UserId, ct))?.DisplayName;
        db.TicketMessages.Add(ticket.Add(TicketAuthors.Seller, caller.UserId, name, request.Body, clock.UtcNow));
        await db.SaveChangesAsync(ct);
        return Results.Ok(await DetailAsync(db, ticket, ct));
    }

    private static async Task<IResult> CloseAsync(Guid ticketId, NotificationsDbContext db, IClock clock, CancellationToken ct)
    {
        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == ticketId, ct);
        if (ticket is null)
        {
            return Error.NotFound().ToProblem();
        }

        ticket.Close(clock.UtcNow);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(ticket));
    }

    private static async Task<IResult> AnnouncementsAsync(NotificationsDbContext db, IClock clock, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.Announcements.AsNoTracking().Where(a => a.StartsAt <= now && (a.EndsAt == null || a.EndsAt > now))
            .OrderByDescending(a => a.StartsAt).Take(20).ToListAsync(ct);
        return Results.Ok(rows
            .OrderBy(a => a.Severity == AnnouncementSeverities.Critical ? 0 : a.Severity == AnnouncementSeverities.Warning ? 1 : 2)
            .Select(a => new ActiveAnnouncement(a.Id, a.Title, a.Body, a.Severity, a.Link, a.StartsAt, a.EndsAt))
            .ToList());
    }
}
