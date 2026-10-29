using System.Text.Json;
using System.Text.Json.Nodes;
using Pishkhan.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Audit.Contracts;

public static class AuditActorTypes
{
    public const string Staff = "staff";
    public const string User = "user";
    public const string System = "system";
}

/// <summary>
/// Who did it. <see cref="Name"/> may be null for users: the audit sink fills in the display name or masked phone.
/// A staff member impersonating a user is recorded as the staff member, with <see cref="OnBehalfOf"/> set to the user.
/// </summary>
public sealed record AuditActor(string Type, Guid? Id, string? Name, string? Ip, Guid? OnBehalfOf = null)
{
    public static readonly AuditActor System = new(AuditActorTypes.System, null, "system", null);

    public static AuditActor Staff(Guid staffId, string? username, string? ip) => new(AuditActorTypes.Staff, staffId, username, ip);

    /// <summary>A panel user, or the staff member impersonating them when <paramref name="impersonatorStaffId"/> is set.</summary>
    public static AuditActor User(Guid userId, Guid? impersonatorStaffId, string? ip) =>
        impersonatorStaffId is { } staffId ? new(AuditActorTypes.Staff, staffId, null, ip, userId) : new(AuditActorTypes.User, userId, null, ip);
}

/// <summary>
/// One append-only audit entry. Modules publish it through their outbox in the same transaction as the change; the Audit module's
/// sink (platform group) stores it. <see cref="EntryId"/> makes redelivery harmless.
/// </summary>
[IntegrationEvent(Topics.AuditEntryRecorded)]
public sealed record AuditEntryRecorded(
    Guid EntryId,
    DateTimeOffset OccurredAt,
    string ActorType,
    Guid? ActorId,
    string? ActorName,
    Guid? OnBehalfOf,
    Guid? WorkspaceId,
    string Action,
    string EntityType,
    string? EntityId,
    string? Reason,
    string? Ip,
    JsonElement? Before,
    JsonElement? After) : IIntegrationEvent
{
    /// <summary>Partition key: the workspace, or one shared key for platform-level entries.</summary>
    public string PartitionKey() => WorkspaceId?.ToString() ?? "platform";
}

public static class AuditEntries
{
    public const string Redacted = "[redacted]";

    /// <summary>Key fragments (lower case, without '_' and '-') whose values are never stored.</summary>
    private static readonly string[] SensitiveFragments =
        ["password", "secret", "token", "totp", "apikey", "authorization", "cookie", "privatekey", "cardnumber", "cvv", "sheba", "iban", "phone"];

    private static readonly HashSet<string> SensitiveKeys = new(StringComparer.Ordinal) { "otp", "pan", "pin", "code" };

    /// <summary>
    /// Builds an entry. <paramref name="before"/> and <paramref name="after"/> are any serializable objects (anonymous types are fine);
    /// values under sensitive keys (passwords, tokens, secrets, phone numbers, card numbers) are replaced with <see cref="Redacted"/>.
    /// </summary>
    public static AuditEntryRecorded Create(
        AuditActor actor, string action, string entityType, string? entityId, Guid? workspaceId, DateTimeOffset now,
        object? before = null, object? after = null, string? reason = null) =>
        new(
            Uuid.NewV7(now), now, actor.Type, actor.Id, actor.Name, actor.OnBehalfOf, workspaceId, action, entityType, entityId,
            Truncate(reason, 500), actor.Ip, Snapshot(before), Snapshot(after));

    public static JsonElement? Snapshot(object? value)
    {
        if (value is null)
        {
            return null;
        }

        var node = value is JsonElement element ? JsonNode.Parse(element.GetRawText()) : JsonSerializer.SerializeToNode(value, value.GetType(), EnvelopeJson.Options);
        if (node is null)
        {
            return null;
        }

        Redact(node);
        return JsonSerializer.SerializeToElement(node, EnvelopeJson.Options);
    }

    public static bool IsSensitive(string key)
    {
        var normalized = key.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
        return SensitiveKeys.Contains(normalized) || SensitiveFragments.Any(f => normalized.Contains(f, StringComparison.Ordinal));
    }

    private static void Redact(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (IsSensitive(key))
                    {
                        obj[key] = child is null ? null : Redacted;
                    }
                    else if (child is not null)
                    {
                        Redact(child);
                    }
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    if (child is not null)
                    {
                        Redact(child);
                    }
                }

                break;
        }
    }

    private static string? Truncate(string? value, int max) => value is null || value.Length <= max ? value : value[..max];
}
