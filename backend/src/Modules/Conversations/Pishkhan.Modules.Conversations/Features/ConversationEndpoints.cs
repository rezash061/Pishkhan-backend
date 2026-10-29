using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Conversations.Realtime;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Conversations.Features;

/// <summary>
/// A conversation in the inbox. <c>windowExpiresAt</c> ends normal replies; until <c>humanAgentUntil</c> a team member can still reply
/// (sent with Meta's HUMAN_AGENT tag); after that the composer is closed until the customer writes again.
/// </summary>
public sealed record ConversationResponse(
    Guid Id, Guid ChannelAccountId, string ChannelKind, Guid ContactId, string Status, Guid? AssigneeId, bool AiPaused,
    DateTimeOffset? LastInboundAt, DateTimeOffset? LastOutboundAt, DateTimeOffset? WindowExpiresAt, DateTimeOffset? HumanAgentUntil, int UnreadCount, string? LastMessagePreview);

public sealed record MessageResponse(
    Guid Id, DateTimeOffset CreatedAt, string Direction, string Kind, string? Text, string Status, string? ErrorCode, string? HandledBy,
    Guid? SentBy, string? Tag, DateTimeOffset? SentAt, DateTimeOffset? ReadAt);

public sealed record QuickReplyOption(string Title, string Payload);

public sealed record ReplyButton(string Title, string? Payload, Uri? Url);

/// <summary>A team member's reply. Text up to 1,000 characters; optional quick replies (13 max) or buttons (3 max, text up to 640).</summary>
public sealed record SendMessageRequest(string Text, IReadOnlyList<QuickReplyOption>? QuickReplies, IReadOnlyList<ReplyButton>? Buttons);

public sealed record AssignRequest(Guid? AssigneeId);

public sealed record ChangeStatusRequest(string Status, string? Reason);

public sealed record AiPauseRequest(bool Paused);

public sealed record NoteRequest(string Text);

public sealed record NoteResponse(Guid Id, Guid AuthorId, string Text, DateTimeOffset CreatedAt);

public sealed record QuickReplyRequest(string Shortcut, string Text);

public sealed record QuickReplyResponse(Guid Id, string Shortcut, string Text, DateTimeOffset UpdatedAt);

internal static class ConversationEndpoints
{
    private const int MaxPageSize = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/conversations").WithTags("Conversations");

        ws.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Conversations, most recent customer message first")
            .WithDescription("Filters: `status`, `assignee` (`me`, `none` or a user id), `channelAccountId`, `unread=true`. Keyset pagination: pass `cursor` from the previous page.")
            .Produces<Page<ConversationResponse>>();

        ws.MapGet("/{conversationId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet("/{conversationId:guid}/messages", MessagesAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Messages, newest first, keyset paginated by (created_at, id)")
            .Produces<Page<MessageResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/{conversationId:guid}/messages", SendAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .WithIdempotency()
            .WithSummary("Send a reply as a team member")
            .WithDescription("Queues the reply (202) and sends it in the background; the status arrives as `message.status`. Outside the 24-hour window a reply within 7 days of the customer's last message goes out with the HUMAN_AGENT tag; later than that it is refused with `conversation.window_closed`.")
            .Produces<MessageResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapPost("/{conversationId:guid}/assign", AssignAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .WithSummary("Assign to a team member, or unassign with `assigneeId: null`")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        ws.MapPost("/{conversationId:guid}/status", ChangeStatusAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .WithSummary("Change status: open, pending, handed_off or closed")
            .WithDescription("`handed_off` also pauses the AI and notifies the team.")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        ws.MapPost("/{conversationId:guid}/ai-pause", AiPauseAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .WithSummary("Pause or resume the AI agent for this conversation")
            .Produces<ConversationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/{conversationId:guid}/read", MarkReadAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Mark the conversation read for the team")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapGet("/{conversationId:guid}/notes", NotesAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Internal notes, oldest first")
            .Produces<IReadOnlyList<NoteResponse>>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/{conversationId:guid}/notes", AddNoteAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .WithSummary("Add an internal note (never sent to the customer)")
            .Produces<NoteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        var quick = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/quick-replies").WithTags("Quick replies");

        quick.MapGet("/", ListQuickRepliesAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Saved replies, by shortcut")
            .Produces<IReadOnlyList<QuickReplyResponse>>();

        quick.MapPost("/", CreateQuickReplyAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .Produces<QuickReplyResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        quick.MapPatch("/{id:guid}", UpdateQuickReplyAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .Produces<QuickReplyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        quick.MapDelete("/{id:guid}", DeleteQuickReplyAsync)
            .RequireWorkspacePermission(Permissions.InboxReply)
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    internal static ConversationResponse ToResponse(Conversation c) => new(
        c.Id, c.ChannelAccountId, c.ChannelKind, c.ContactId, c.Status, c.AssigneeId, c.AiPaused, c.LastInboundAt, c.LastOutboundAt,
        c.WindowExpiresAt, c.LastInboundAt + Conversation.HumanAgentWindow, c.UnreadCount, c.LastMessagePreview);

    internal static MessageResponse ToResponse(Message m) =>
        new(m.Id, m.CreatedAt, m.Direction, m.Kind, m.Text, m.Status, m.ErrorCode, m.HandledBy, m.SentBy, m.Tag, m.SentAt, m.ReadAt);

    private static async Task<IResult> ListAsync(
        Guid workspaceId, string? status, string? assignee, Guid? channelAccountId, bool? unread, string? cursor, int? limit,
        ConversationsDbContext db, ICurrentUser caller, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 30, 1, MaxPageSize);
        var query = db.Conversations.AsNoTracking();
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(c => c.Status == status);
        }

        switch (assignee)
        {
            case null or "":
                break;
            case "me":
                query = query.Where(c => c.AssigneeId == caller.UserId);
                break;
            case "none":
                query = query.Where(c => c.AssigneeId == null);
                break;
            default:
                if (!Guid.TryParse(assignee, out var assigneeId))
                {
                    return Invalid("assignee", "validation.assignee.invalid");
                }

                query = query.Where(c => c.AssigneeId == assigneeId);
                break;
        }

        if (channelAccountId is { } account)
        {
            query = query.Where(c => c.ChannelAccountId == account);
        }

        if (unread == true)
        {
            query = query.Where(c => c.UnreadCount > 0);
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(c => EF.Functions.LessThan(ValueTuple.Create(c.LastInboundAt, c.Id), ValueTuple.Create((DateTimeOffset?)at, id)));
        }

        var rows = await query.OrderByDescending(c => c.LastInboundAt).ThenByDescending(c => c.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].LastInboundAt ?? default, rows[take - 1].Id) : null;
        return Results.Ok(new Page<ConversationResponse>([.. rows.Take(take).Select(ToResponse)], next));
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid conversationId, ConversationsDbContext db, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == conversationId, ct);
        return c is null ? Error.NotFound().ToProblem() : Results.Ok(ToResponse(c));
    }

    private static async Task<IResult> MessagesAsync(Guid workspaceId, Guid conversationId, string? cursor, int? limit, ConversationsDbContext db, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var take = Math.Clamp(limit ?? 50, 1, MaxPageSize);
        var query = db.Messages.AsNoTracking().Where(m => m.ConversationId == conversationId);
        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(m => EF.Functions.LessThan(ValueTuple.Create(m.CreatedAt, m.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1).ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<MessageResponse>([.. rows.Take(take).Select(ToResponse)], next));
    }

    private static async Task<IResult> SendAsync(Guid workspaceId, Guid conversationId, SendMessageRequest request, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (ValidateReply(request) is { } invalid)
        {
            return invalid;
        }

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return Error.NotFound().ToProblem();
        }

        var now = clock.UtcNow;
        if (!conversation.IsWindowOpen(now) && !conversation.IsHumanAgentWindowOpen(now))
        {
            return Error.Unprocessable("conversation.window_closed").ToProblem();
        }

        var content = new OutboundContent(
            request.Text.Trim(),
            QuickReplies: request.QuickReplies?.Select(q => new QuickReply(q.Title, q.Payload)).ToList(),
            Buttons: request.Buttons?.Select(b => b.Url is { } url ? MessageButton.Link(b.Title, url) : MessageButton.Postback(b.Title, b.Payload ?? b.Title)).ToList());
        var message = Message.QueueOutbound(conversation.WorkspaceId, conversation.Id, content, HandledBy.Human, caller.UserId, now);
        db.Messages.Add(message);
        db.Publish(new OutboundMessageRequested(conversation.Id, message.Id, message.CreatedAt, HandledBy.Human, caller.UserId), conversation.Id.ToString());
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(message), statusCode: StatusCodes.Status202Accepted);
    }

    private static async Task<IResult> AssignAsync(Guid workspaceId, Guid conversationId, AssignRequest request, ConversationsDbContext db, IWorkspaceAccessResolver access, IInboxNotifier notifier, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (request.AssigneeId is { } userId && await access.ResolveAsync(userId, workspaceId, ct) is null)
        {
            return Invalid("assigneeId", "validation.assignee.not_member");
        }

        if (conversation.Assign(request.AssigneeId))
        {
            await db.SaveChangesAsync(ct);
            await notifier.ConversationUpdatedAsync(conversation, ct);
        }

        return Results.Ok(ToResponse(conversation));
    }

    private static async Task<IResult> ChangeStatusAsync(Guid workspaceId, Guid conversationId, ChangeStatusRequest request, ConversationsDbContext db, ICurrentUser caller, IInboxNotifier notifier, CancellationToken ct)
    {
        if (!ConversationStatuses.All.Contains(request.Status))
        {
            return Invalid("status", "validation.status.invalid");
        }

        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (!conversation.ChangeStatus(request.Status))
        {
            return Results.Ok(ToResponse(conversation));
        }

        // A handoff reaches the panel through the handoff event (realtime and, later, notifications); other changes are pushed here.
        var handedOff = request.Status == ConversationStatuses.HandedOff;
        if (handedOff)
        {
            conversation.SetAiPaused(true);
            db.Publish(new HandoffRequested(conversation.Id, string.IsNullOrWhiteSpace(request.Reason) ? "manual" : request.Reason.Trim(), caller.UserId, null), conversation.Id.ToString());
        }

        await db.SaveChangesAsync(ct);
        if (!handedOff)
        {
            await notifier.ConversationUpdatedAsync(conversation, ct);
        }

        return Results.Ok(ToResponse(conversation));
    }

    private static async Task<IResult> AiPauseAsync(Guid workspaceId, Guid conversationId, AiPauseRequest request, ConversationsDbContext db, IInboxNotifier notifier, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (conversation.SetAiPaused(request.Paused))
        {
            await db.SaveChangesAsync(ct);
            await notifier.ConversationUpdatedAsync(conversation, ct);
        }

        return Results.Ok(ToResponse(conversation));
    }

    private static async Task<IResult> MarkReadAsync(Guid workspaceId, Guid conversationId, ConversationsDbContext db, IInboxNotifier notifier, CancellationToken ct)
    {
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId, ct);
        if (conversation is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (conversation.UnreadCount > 0)
        {
            conversation.MarkRead();
            await db.SaveChangesAsync(ct);
            await notifier.ConversationUpdatedAsync(conversation, ct);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> NotesAsync(Guid workspaceId, Guid conversationId, ConversationsDbContext db, CancellationToken ct)
    {
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        return Results.Ok(await db.Notes.AsNoTracking().Where(n => n.ConversationId == conversationId).OrderBy(n => n.CreatedAt).Take(500)
            .Select(n => new NoteResponse(n.Id, n.AuthorId, n.Text, n.CreatedAt)).ToListAsync(ct));
    }

    private static async Task<IResult> AddNoteAsync(Guid workspaceId, Guid conversationId, NoteRequest request, ConversationsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 2000)
        {
            return Invalid("text", "validation.note.length");
        }

        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId, ct))
        {
            return Error.NotFound().ToProblem();
        }

        var note = ConversationNote.Create(workspaceId, conversationId, caller.UserId, request.Text.Trim(), clock.UtcNow);
        db.Notes.Add(note);
        await db.SaveChangesAsync(ct);
        return Results.Json(new NoteResponse(note.Id, note.AuthorId, note.Text, note.CreatedAt), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ListQuickRepliesAsync(Guid workspaceId, ConversationsDbContext db, CancellationToken ct) =>
        Results.Ok(await db.QuickReplies.AsNoTracking().OrderBy(q => q.Shortcut)
            .Select(q => new QuickReplyResponse(q.Id, q.Shortcut, q.Text, q.UpdatedAt)).ToListAsync(ct));

    private static async Task<IResult> CreateQuickReplyAsync(Guid workspaceId, QuickReplyRequest request, ConversationsDbContext db, IClock clock, CancellationToken ct)
    {
        if (ValidateQuickReply(request) is { } invalid)
        {
            return invalid;
        }

        var reply = QuickReplyTemplate.Create(workspaceId, NormalizeShortcut(request.Shortcut), request.Text.Trim(), clock.UtcNow);
        db.QuickReplies.Add(reply);
        return await SaveQuickReplyAsync(db, reply, StatusCodes.Status201Created, ct);
    }

    private static async Task<IResult> UpdateQuickReplyAsync(Guid workspaceId, Guid id, QuickReplyRequest request, ConversationsDbContext db, CancellationToken ct)
    {
        if (ValidateQuickReply(request) is { } invalid)
        {
            return invalid;
        }

        var reply = await db.QuickReplies.FirstOrDefaultAsync(q => q.Id == id, ct);
        if (reply is null)
        {
            return Error.NotFound().ToProblem();
        }

        reply.Shortcut = NormalizeShortcut(request.Shortcut);
        reply.Text = request.Text.Trim();
        return await SaveQuickReplyAsync(db, reply, StatusCodes.Status200OK, ct);
    }

    private static async Task<IResult> DeleteQuickReplyAsync(Guid workspaceId, Guid id, ConversationsDbContext db, CancellationToken ct) =>
        await db.QuickReplies.Where(q => q.Id == id).ExecuteDeleteAsync(ct) == 0 ? Error.NotFound().ToProblem() : Results.NoContent();

    private static async Task<IResult> SaveQuickReplyAsync(ConversationsDbContext db, QuickReplyTemplate reply, int status, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("quick_reply.shortcut_taken").ToProblem();
        }

        return Results.Json(new QuickReplyResponse(reply.Id, reply.Shortcut, reply.Text, reply.UpdatedAt), statusCode: status);
    }

    private static string NormalizeShortcut(string shortcut) => shortcut.Trim().TrimStart('/').ToLowerInvariant();

    private static IResult? ValidateQuickReply(QuickReplyRequest request)
    {
        var shortcut = NormalizeShortcut(request.Shortcut ?? string.Empty);
        if (shortcut.Length is 0 or > 40 || shortcut.Any(char.IsWhiteSpace))
        {
            return Invalid("shortcut", "validation.shortcut.invalid");
        }

        return string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1000 ? Invalid("text", "validation.text.length") : null;
    }

    private static IResult? ValidateReply(SendMessageRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Length > 1000)
        {
            return Invalid("text", "validation.text.length");
        }

        if (request.QuickReplies is { Count: > 0 } && request.Buttons is { Count: > 0 })
        {
            return Invalid("buttons", "validation.reply.buttons_and_quick_replies");
        }

        if (request.QuickReplies is { } quick && (quick.Count > 13 || quick.Any(q => string.IsNullOrWhiteSpace(q.Title) || q.Title.Length > 20 || string.IsNullOrWhiteSpace(q.Payload))))
        {
            return Invalid("quickReplies", "validation.reply.quick_replies");
        }

        if (request.Buttons is { } buttons && (buttons.Count > 3 || request.Text.Length > 640
            || buttons.Any(b => string.IsNullOrWhiteSpace(b.Title) || b.Title.Length > 20 || b.Url is { Scheme: not "https" })))
        {
            return Invalid("buttons", "validation.reply.buttons");
        }

        return null;
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation(code, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] }).ToProblem();
}
