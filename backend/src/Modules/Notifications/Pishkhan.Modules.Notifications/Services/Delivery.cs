using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Timers;
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

/// <summary>Which channels a write may use (each still subject to the recipient's preferences).</summary>
[Flags]
internal enum Deliver
{
    InApp = 1,
    Push = 2,
    Sms = 4,
    All = InApp | Push | Sms,
}

/// <summary>
/// Turns one event into notifications: picks the recipients (one user, or the members with the kind's permission), applies each
/// one's preferences, adds the in-app rows (and their realtime event) and collects the web push and SMS to send after the commit.
/// </summary>
internal sealed class NotificationWriter(NotificationsDbContext db, IMembershipQueries members, IClock clock)
{
    public List<(Guid UserId, string Text)> PendingSms { get; } = [];

    public List<(Guid UserId, PushPayload Payload)> PendingPush { get; } = [];

    /// <summary>The recipients whose preferences have SMS on for <paramref name="kind"/>.</summary>
    public async Task<IReadOnlyList<Guid>> SmsRecipientsAsync(Guid workspaceId, string kind, CancellationToken ct, Guid? except = null)
    {
        var info = NotificationCatalog.Find(kind);
        var recipients = (await members.GetMembersWithPermissionAsync(workspaceId, info.RecipientPermission, ct)).Where(r => r != except).ToList();
        var preferences = await db.Preferences.Where(p => p.Kind == kind && recipients.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, ct);
        return [.. recipients.Where(r => NotificationCatalog.Effective(info, preferences.GetValueOrDefault(r)).Sms)];
    }

    public async Task WriteAsync(
        Guid workspaceId, Guid? userId, string kind, string title, string? body, string? link, CancellationToken ct, Guid? except = null, Deliver channels = Deliver.All)
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
            if (choice.InApp && channels.HasFlag(Deliver.InApp))
            {
                var notification = Notification.Create(workspaceId, recipient, kind, title, body, link, now);
                db.Notifications.Add(notification);
                db.Publish(
                    new NotificationCreated(workspaceId, recipient, notification.Id, notification.Kind, notification.Title, notification.Body, notification.Link, now),
                    recipient.ToString(), workspaceId: workspaceId);
            }

            if (choice.Push && channels.HasFlag(Deliver.Push))
            {
                PendingPush.Add((recipient, new PushPayload(kind, title, body, link, workspaceId)));
            }

            if (choice.Sms && channels.HasFlag(Deliver.Sms))
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

/// <summary>Base of the notification handlers: rows in the transaction, web push and SMS after the commit (best effort, never retried).</summary>
internal abstract class NotifyingHandler<TEvent>(
    NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, WebPushSender push, ILogger logger)
    : IdempotentHandler<TEvent, NotificationsDbContext>(db)
    where TEvent : IIntegrationEvent
{
    protected NotificationWriter Writer => writer;

    /// <summary>A retried transaction runs this again, so the SMS list starts empty each time.</summary>
    protected sealed override Task HandleOnceAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        writer.PendingSms.Clear();
        writer.PendingPush.Clear();
        return NotifyAsync(message, envelope, cancellationToken);
    }

    protected abstract Task NotifyAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken);

    protected override async Task AfterCommitAsync(TEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        foreach (var (userId, payload) in writer.PendingPush)
        {
            try
            {
                await push.SendAsync(userId, payload, cancellationToken);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogWarning(e, "Web push to user {UserId} failed", userId);
            }
        }

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
internal sealed class NotificationRequestedHandler(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, WebPushSender push, ILogger<NotificationRequestedHandler> logger)
    : NotifyingHandler<NotificationRequested>(db, writer, users, sms, push, logger)
{
    protected override Task NotifyAsync(NotificationRequested message, EventEnvelope envelope, CancellationToken cancellationToken) =>
        Writer.WriteAsync(message.WorkspaceId, message.UserId, message.Kind, message.Title, message.Body, message.Link, cancellationToken);
}

public sealed class NotificationsOptions
{
    public const string Section = "Notifications";

    /// <summary>A handed-off conversation still waiting this long sends the SMS (plan 9.15: "handoff waiting > N minutes").</summary>
    public TimeSpan HandoffSmsAfter { get; set; } = TimeSpan.FromMinutes(10);
}

internal sealed record HandoffWaitingTimer(Guid ConversationId, Guid? RequestedBy);

/// <summary>
/// A conversation handed to the team: everyone who can reply (except the member who handed it over) gets it in-app and by push now.
/// SMS waits: a timer checks after <see cref="NotificationsOptions.HandoffSmsAfter"/> whether nobody has answered yet.
/// </summary>
internal sealed class HandoffNotificationHandler(
    NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, WebPushSender push, IOptions<NotificationsOptions> options, IClock clock,
    ILogger<HandoffNotificationHandler> logger)
    : NotifyingHandler<HandoffRequested>(db, writer, users, sms, push, logger)
{
    public const string TimerKind = "notifications.handoff-waiting";

    protected override async Task NotifyAsync(HandoffRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.WorkspaceId is not { } workspaceId)
        {
            return;
        }

        await Writer.WriteAsync(
            workspaceId, null, NotificationKinds.Handoff, HandoffWaitingHandler.Title, message.SuggestedReply is null ? null : "پیشنهاد پاسخ: " + message.SuggestedReply,
            $"/inbox/{message.ConversationId}", cancellationToken, except: message.RequestedBy, channels: Deliver.InApp | Deliver.Push);

        var key = $"{TimerKind}:{message.ConversationId:N}";
        if ((await Writer.SmsRecipientsAsync(workspaceId, NotificationKinds.Handoff, cancellationToken, message.RequestedBy)).Count > 0
            && !await Db.Timers.AnyAsync(t => t.DedupeKey == key && t.Status == TimerStatus.Pending, cancellationToken))
        {
            TimerScheduler.Schedule(Db, TimerKind, clock.UtcNow + options.Value.HandoffSmsAfter, new HandoffWaitingTimer(message.ConversationId, message.RequestedBy), key, workspaceId);
        }
    }
}

/// <summary>The handoff timer: SMS the members who want it when the conversation is still handed off and the customer spoke last.</summary>
internal sealed class HandoffWaitingHandler(
    NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, WebPushSender push, IConversationQueries conversations,
    ILogger<HandoffWaitingHandler> logger)
    : NotifyingHandler<TimerFired>(db, writer, users, sms, push, logger)
{
    public const string Title = "یک گفتگو منتظر پاسخ تیم است";

    protected override async Task NotifyAsync(TimerFired message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (message.Kind != HandoffNotificationHandler.TimerKind || message.WorkspaceId is not { } workspaceId)
        {
            return;
        }

        var timer = message.Payload.Deserialize<HandoffWaitingTimer>(EnvelopeJson.Options)!;
        var conversation = await conversations.GetAsync(timer.ConversationId, cancellationToken);
        if (conversation is not { Status: ConversationStatuses.HandedOff })
        {
            return;
        }

        var last = await conversations.RecentMessagesAsync(timer.ConversationId, 1, cancellationToken);
        if (last.Count == 0 || last[^1].Direction != MessageDirections.Inbound)
        {
            return;
        }

        await Writer.WriteAsync(workspaceId, null, NotificationKinds.Handoff, Title, null, $"/inbox/{timer.ConversationId}", cancellationToken, except: timer.RequestedBy, channels: Deliver.Sms);
    }
}

/// <summary>An Instagram account that needs the owner. Back to active, or disconnected by a member, needs no notification.</summary>
internal sealed class ChannelNotificationHandler(NotificationsDbContext db, NotificationWriter writer, IUserDirectory users, ISmsSender sms, WebPushSender push, ILogger<ChannelNotificationHandler> logger)
    : NotifyingHandler<ChannelStatusChanged>(db, writer, users, sms, push, logger)
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
            : Writer.WriteAsync(message.WorkspaceId, null, NotificationKinds.Channel, title, null, "/channels", cancellationToken);
    }
}
