using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Conversations.Realtime;

/// <summary>Event names on <c>/hubs/inbox</c> (overview section 6, realtime events).</summary>
public static class InboxEvents
{
    public const string ConversationUpdated = "conversation.updated";
    public const string MessageCreated = "message.created";
    public const string MessageStatus = "message.status";
    public const string HandoffRequested = "handoff.requested";
    public const string ChannelStatus = "channel.status";
}

internal static class InboxGroups
{
    public static string Workspace(Guid workspaceId) => $"ws:{workspaceId:N}";

    public static string Conversation(Guid conversationId) => $"conv:{conversationId:N}";
}

/// <summary>
/// The panel's live inbox. Connect with a 5-minute realtime token (<c>POST /api/v1/auth/realtime-token</c>), then call
/// <c>JoinWorkspace</c> for the inbox list and channel banners and <c>JoinConversation</c> for an open conversation.
/// Membership and the <c>inbox.read</c> permission are checked on every join; the server only pushes, clients never send events.
/// </summary>
[Authorize(Policy = AuthPolicies.Realtime)]
public sealed class InboxHub(IWorkspaceAccessResolver access, ITenantContext tenant, ConversationsDbContext db) : Hub
{
    public const string Path = "/hubs/inbox";

    public async Task JoinWorkspace(Guid workspaceId)
    {
        await EnsureAccessAsync(workspaceId);
        await Groups.AddToGroupAsync(Context.ConnectionId, InboxGroups.Workspace(workspaceId), Context.ConnectionAborted);
    }

    public Task LeaveWorkspace(Guid workspaceId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, InboxGroups.Workspace(workspaceId), Context.ConnectionAborted);

    public async Task JoinConversation(Guid workspaceId, Guid conversationId)
    {
        await EnsureAccessAsync(workspaceId);
        tenant.SetWorkspace(workspaceId);
        if (!await db.Conversations.AnyAsync(c => c.Id == conversationId, Context.ConnectionAborted))
        {
            throw new HubException("common.not_found");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, InboxGroups.Conversation(conversationId), Context.ConnectionAborted);
    }

    public Task LeaveConversation(Guid conversationId) =>
        Groups.RemoveFromGroupAsync(Context.ConnectionId, InboxGroups.Conversation(conversationId), Context.ConnectionAborted);

    private async Task EnsureAccessAsync(Guid workspaceId)
    {
        if (!Guid.TryParse(Context.User?.FindFirst(PishkhanClaims.Subject)?.Value, out var userId)
            || await access.ResolveAsync(userId, workspaceId, Context.ConnectionAborted) is not { Suspended: false } membership
            || !membership.Permissions.Contains(Permissions.InboxRead))
        {
            // The same answer for "no such workspace" and "not allowed", as in the REST API.
            throw new HubException("common.not_found");
        }
    }
}
