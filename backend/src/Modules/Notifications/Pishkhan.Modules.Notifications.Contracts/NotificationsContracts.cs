namespace Pishkhan.Modules.Notifications.Contracts;

/// <summary>
/// Kinds of notification a user can tune (backend plan 9.15). Producers publish <see cref="Pishkhan.Contracts.NotificationRequested"/>
/// with one of these; an unknown kind is delivered in-app only.
/// </summary>
public static class NotificationKinds
{
    /// <summary>A conversation was handed to the team.</summary>
    public const string Handoff = "handoff";

    /// <summary>An automation's «notify the team» action.</summary>
    public const string Automation = "automation";

    /// <summary>An Instagram account needs the owner: reconnect, expiring token, disconnected.</summary>
    public const string Channel = "channel";

    public static readonly IReadOnlyList<string> All = [Handoff, Automation, Channel];
}
