namespace Pishkhan.Contracts;

/// <summary>Asks every host to drop cached entries with these tags (backend plan section 5, rule 3).</summary>
[IntegrationEvent(Topics.CacheInvalidated)]
public sealed record CacheInvalidated(IReadOnlyList<string> Tags) : IIntegrationEvent;

/// <summary>A row from <c>platform.timers</c> came due. Consumers filter by <see cref="Kind"/> and re-check conditions.</summary>
[IntegrationEvent(Topics.TimerFired)]
public sealed record TimerFired(Guid TimerId, string Kind, Guid? WorkspaceId, System.Text.Json.JsonElement Payload, DateTimeOffset DueAt) : IIntegrationEvent;

/// <summary>
/// Asks the notifications module to tell the team something (handoff, a rule's "notify team" action). <see cref="UserId"/> null means
/// every member who can read the inbox. Consumed from BE-1.14 on.
/// </summary>
[IntegrationEvent(Topics.NotificationRequested)]
public sealed record NotificationRequested(Guid WorkspaceId, Guid? UserId, string Kind, string Title, string? Body, string? Link) : IIntegrationEvent;
