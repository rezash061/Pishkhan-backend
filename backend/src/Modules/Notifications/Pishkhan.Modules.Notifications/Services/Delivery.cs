using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Notifications.Contracts;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Notifications.Services;

/// <summary>
/// Turns one event into notifications: picks the recipients (one user, or the members with the kind's permission), applies each
/// one's preferences, adds the in-app rows and collects the SMS to send after the commit.
/// </summary>
internal sealed class NotificationWriter(NotificationsDbContext db, IMembershipQueries members, IClock clock)
{
    public List<(Guid UserId, string Text)> PendingSms { get; } = [];

    public async Task WriteAsync(Guid workspaceId, Guid? userId, string kind, string title, string? body, string? link, CancellationToken ct, Guid? except = null)
    {
        var info = NotificationCatalog.Find(kind);
        IReadOnlyList<Guid> recipients = userId is { } one ? [one] : await members.GetMembersWithPermissionAsync(workspaceId, info.RecipientPermission, ct);
        recipients = [.. recipients.Where(r => r != except)];
        if (recipients.Count == 0)
        {
            return;
        }

        var preferences = await db.Preferences.Where(p => p.Kind == kind && recipients.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        var smsLog = await db.SmsLog.Where(s => s.Kind == kind && recipients.Contains(s.UserId)).ToDictionaryAsync(s => s.UserId, ct);
        var now = clock.UtcNow;
        foreach (var recipient in recipients)
        {
            var choice = NotificationCatalog.Effective(info, preferences.GetValueOrDefault(recipient));
            if (choice.InApp)
            {
                db.Notifications.Add(Notification.Create(workspaceId, recipient, kind, title, body, link, now));
            }

            if (choice.Sms)
            {
                if (!smsLog.TryGetValue(recipient, out var log))
                {
                    log = SmsLog.Create(workspaceId, recipient, kind);
                    db.SmsLog.Add(log);
                }

                if (log.TryClaim(now))
                {
                    PendingSms.Add((recipient, $"پیشخوان: {title}"));
                }
            }
        }
    }
}

/// <summary>Base of the notification handlers: rows in the transaction, SMS after the commit (best effort, never retried).</summary>
internal abstract class NotifyingHandler<TEvent>(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, ILogger logger)
    : IdempotentHandler<TEvent, NotificationsDbContext>(db)
    where TEvent : IIntegrationEvent
{
    protected NotificationWriter Writer => writer;

    /// <summary>A retried transaction runs this again, so the SMS list starts empty each time.</summary>
    protected sealed override Task HandleOnceAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        writer.PendingSms.Clear();
        return NotifyAsync(message, envelope, cancellationToken);
    }

    protected abstract Task NotifyAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken);

    protected override async Task AfterCommitAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var (userId, text) in writer.PendingSms)
        {
            try
            {
                if (await users.GetPhoneAsync(userId, cancellationToken) is { } phone)
                {
                    await sms.SendAsync(new SmsMessage(phone, text), cancellationToken);
                }
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Notification SMS to user {UserId} failed", userId);
            }
        }
    }
}

/// <summary>Generic requests from any module (for example the automations' «notify the team» action).</summary>
internal sealed class NotificationRequestedHandler(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, ILogger<NotificationRequestedHandler> logger)
    : NotifyingHandler<NotificationRequested>(db, writer, users, sms, logger)
{
    protected override Task NotifyAsync(NotificationRequested message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Writer.WriteAsync(message.WorkspaceId, message.UserId, message.Kind, message.Title, message.Body, message.Link, cancellationToken);
}

/// <summary>A conversation handed to the team: everyone who can reply, except the member who handed it over.</summary>
internal sealed class HandoffNotificationHandler(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, ILogger<HandoffNotificationHandler> logger)
    : NotifyingHandler<HandoffRequested>(db, writer, users, sms, logger)
{
    protected override Task NotifyAsync(HandoffRequested message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        envelope.WorkspaceId is { } workspaceId
            ? Writer.WriteAsync(
                workspaceId, null, NotificationKinds.Handoff, "یک گفتگو منتظر پاسخ تیم است", message.SuggestedReply is null ? null : "پیشنهاد پاسخ: " + message.SuggestedReply,
                $"/inbox/{message.ConversationId}", cancellationToken, except: message.RequestedBy)
            : Task.CompletedTask;
}

/// <summary>An Instagram account that needs the owner. Back to active, or disconnected by a member, needs no notification.</summary>
internal sealed class ChannelNotificationHandler(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, ILogger<ChannelNotificationHandler> logger)
    : NotifyingHandler<ChannelStatusChanged>(db, writer, users, sms, logger)
{
    protected override Task NotifyAsync(ChannelStatusChanged message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var title = message.Status switch
        {
            ChannelStatuses.ReauthRequired => "اتصال اینستاگرام قطع شده؛ دوباره وصل کنید",
            ChannelStatuses.TokenExpiring => "اتصال اینستاگرام به‌زودی منقضی می‌شود",
            ChannelStatuses.Disconnected when message.Reason != "disconnected_by_user" => "دسترسی پیشخوان به حساب اینستاگرام برداشته شد",
            _ => null,
        };
        return title is null
            ? Task.CompletedTask
            : Writer.WriteAsync(message.WorkspaceId, null, NotificationKinds.Channel, title, null, "/settings/channels", cancellationToken);
    }
}
