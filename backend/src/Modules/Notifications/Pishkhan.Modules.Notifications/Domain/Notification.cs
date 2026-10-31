using Pishkhan.Modules.Notifications.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Notifications.Domain;

/// <summary>One in-app notification for one member of a workspace.</summary>
public sealed class Notification : Entity<Guid>, ITenantOwned
{
    private Notification()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;

    public string? Body { get; private set; }

    /// <summary>A panel path, for example <c>/inbox/{conversationId}</c>.</summary>
    public string? Link { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public static Notification Create(Guid workspaceId, Guid userId, string kind, string title, string? body, string? link, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        UserId = userId,
        Kind = kind,
        Title = Truncate(title, 200)!,
        Body = Truncate(body, 1000),
        Link = Truncate(link, 300),
        CreatedAt = now,
    };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}

/// <summary>A member's choice for one kind in one workspace. A missing row means the kind's defaults.</summary>
public sealed class NotificationPreference : ITenantOwned
{
    private NotificationPreference()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public bool InApp { get; private set; }

    /// <summary>Web push. Stored now; used once push subscriptions exist.</summary>
    public bool Push { get; private set; }

    public bool Sms { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationPreference Create(Guid workspaceId, Guid userId, string kind) => new() { WorkspaceId = workspaceId, UserId = userId, Kind = kind };

    public void Set(bool inApp, bool push, bool sms, DateTimeOffset now)
    {
        InApp = inApp;
        Push = push;
        Sms = sms;
        UpdatedAt = now;
    }
}

/// <summary>When a member last got an SMS of a kind: at most one every <see cref="Cooldown"/>, so a busy evening is not twenty texts.</summary>
public sealed class SmsLog : ITenantOwned
{
    public static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    private SmsLog()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public DateTimeOffset LastSentAt { get; private set; }

    public static SmsLog Create(Guid workspaceId, Guid userId, string kind) => new() { WorkspaceId = workspaceId, UserId = userId, Kind = kind };

    /// <summary>True (and records it) when the cooldown is over.</summary>
    public bool TryClaim(DateTimeOffset now)
    {
        if (now - LastSentAt < Cooldown)
        {
            return false;
        }

        LastSentAt = now;
        return true;
    }
}

/// <summary>Channels on for a kind.</summary>
public sealed record ChannelChoice(bool InApp, bool Push, bool Sms);

/// <summary>
/// What each kind is, who gets it and its defaults. SMS costs money and is noisy, so only critical kinds offer it (plan 9.15:
/// handoff waiting, payment received, channel disconnected). Web push is unreliable in Iran, so in-app stays on by default.
/// </summary>
public sealed record NotificationKind(string Kind, string Label, string RecipientPermission, bool SmsAvailable, ChannelChoice Defaults);

public static class NotificationCatalog
{
    public static readonly IReadOnlyList<NotificationKind> Kinds =
    [
        new(NotificationKinds.Handoff, "گفتگوی سپرده‌شده به تیم", Permissions.InboxReply, SmsAvailable: true, new ChannelChoice(true, true, false)),
        new(NotificationKinds.Automation, "اعلان اتوماسیون‌ها", Permissions.InboxReply, SmsAvailable: false, new ChannelChoice(true, true, false)),
        new(NotificationKinds.Channel, "وضعیت اتصال اینستاگرام", Permissions.ChannelsManage, SmsAvailable: true, new ChannelChoice(true, true, true)),
    ];

    /// <summary>Kinds not in the catalog: in-app only, to members who can read the inbox.</summary>
    public static NotificationKind Find(string kind) =>
        Kinds.FirstOrDefault(k => k.Kind == kind) ?? new NotificationKind(kind, kind, Permissions.InboxRead, SmsAvailable: false, new ChannelChoice(true, false, false));

    /// <summary>A preference row (or the defaults), with SMS off where the kind does not offer it.</summary>
    public static ChannelChoice Effective(NotificationKind kind, NotificationPreference? preference) =>
        preference is null ? kind.Defaults : new ChannelChoice(preference.InApp, preference.Push, preference.Sms && kind.SmsAvailable);
}
