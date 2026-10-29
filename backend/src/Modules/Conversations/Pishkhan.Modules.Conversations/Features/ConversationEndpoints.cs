using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Conversations.Features;

public sealed record ConversationResponse(
    Guid Id, Guid ChannelAccountId, string ChannelKind, Guid ContactId, string Status, Guid? AssigneeId, bool AiPaused,
    DateTimeOffset? LastInboundAt, DateTimeOffset? LastOutboundAt, DateTimeOffset? WindowExpiresAt, int UnreadCount, string? LastMessagePreview);

public sealed record MessageResponse(Guid Id, DateTimeOffset CreatedAt, string Direction, string Kind, string? Text, string Status, string? ErrorCode, string? HandledBy, DateTimeOffset? SentAt);


internal static class ConversationEndpoints
{
    private const int MaxPageSize = 100;

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}/conversations").WithTags("Conversations");

        ws.MapGet("/", ListAsync)
            .RequireWorkspacePermission(Permissions.InboxRead)
            .WithSummary("Conversations, most recent customer message first")
            .WithDescription("Keyset pagination: pass `cursor` from the previous page.")
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
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, string? status, string? cursor, int? limit, ConversationsDbContext db, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? 30, 1, MaxPageSize);
        var query = db.Conversations.AsNoTracking();
        if (!string.IsNullOrEmpty(status))
        {
            query = query.Where(c => c.Status == status);
        }

        if (Cursor.TryRead(cursor, out var at, out var id))
        {
            query = query.Where(c => EF.Functions.LessThan(ValueTuple.Create(c.LastInboundAt, c.Id), ValueTuple.Create((DateTimeOffset?)at, id)));
        }

        var rows = await query.OrderByDescending(c => c.LastInboundAt).ThenByDescending(c => c.Id).Take(take + 1)
            .Select(c => new ConversationResponse(c.Id, c.ChannelAccountId, c.ChannelKind, c.ContactId, c.Status, c.AssigneeId, c.AiPaused, c.LastInboundAt, c.LastOutboundAt, c.WindowExpiresAt, c.UnreadCount, c.LastMessagePreview))
            .ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].LastInboundAt ?? default, rows[take - 1].Id) : null;
        return Results.Ok(new Page<ConversationResponse>(rows.Take(take).ToList(), next));
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid conversationId, ConversationsDbContext db, CancellationToken ct)
    {
        var c = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(x => x.Id == conversationId, ct);
        return c is null
            ? Error.NotFound().ToProblem()
            : Results.Ok(new ConversationResponse(c.Id, c.ChannelAccountId, c.ChannelKind, c.ContactId, c.Status, c.AssigneeId, c.AiPaused, c.LastInboundAt, c.LastOutboundAt, c.WindowExpiresAt, c.UnreadCount, c.LastMessagePreview));
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

        var rows = await query.OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id).Take(take + 1)
            .Select(m => new MessageResponse(m.Id, m.CreatedAt, m.Direction, m.Kind, m.Text, m.Status, m.ErrorCode, m.HandledBy, m.SentAt))
            .ToListAsync(ct);
        var next = rows.Count > take ? Cursor.Write(rows[take - 1].CreatedAt, rows[take - 1].Id) : null;
        return Results.Ok(new Page<MessageResponse>(rows.Take(take).ToList(), next));
    }
}

/// <summary>Opaque keyset cursor: base64url of "unix-microseconds:guid".</summary>
internal static class Cursor
{
    public static string Write(DateTimeOffset at, Guid id) =>
        Infrastructure.Storage.Base64Url.Encode(System.Text.Encoding.ASCII.GetBytes(FormattableString.Invariant($"{at.ToUnixTimeMilliseconds() * 1000 + at.Ticks / 10 % 1000}:{id:N}")));

    public static bool TryRead(string? cursor, out DateTimeOffset at, out Guid id)
    {
        at = default;
        id = default;
        if (string.IsNullOrEmpty(cursor))
        {
            return false;
        }

        try
        {
            var parts = System.Text.Encoding.ASCII.GetString(Infrastructure.Storage.Base64Url.Decode(cursor)).Split(':');
            if (parts.Length != 2 || !long.TryParse(parts[0], System.Globalization.CultureInfo.InvariantCulture, out var micros) || !Guid.TryParse(parts[1], out id))
            {
                return false;
            }

            at = DateTimeOffset.UnixEpoch.AddTicks(micros * 10);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
