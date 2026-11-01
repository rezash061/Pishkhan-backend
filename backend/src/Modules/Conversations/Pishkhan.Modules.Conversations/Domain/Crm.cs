using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Domain;

/// <summary>
/// A tag's look in the CRM (colour, description). Contacts hold tags by name, so automation can add a tag nobody defined; a definition
/// only decorates it, and renaming or deleting one rewrites the contacts that carry it.
/// </summary>
public sealed class ContactTag : Entity<Guid>, ITenantOwned
{
    public const int MaxName = 50;

    public Guid WorkspaceId { get; init; }

    public string Name { get; set; } = string.Empty;

    /// <summary>A palette key the panel maps to a colour (gray, red, orange, yellow, green, teal, blue, purple, pink).</summary>
    public string? Color { get; set; }

    public string? Description { get; set; }

    public static ContactTag Create(Guid workspaceId, string name, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Name = name,
        CreatedAt = now,
    };
}

/// <summary>A custom field the team can see and edit on every contact. Values live on the contact under <see cref="Key"/>.</summary>
public sealed class ContactFieldDefinition : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public string Key { get; init; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    /// <summary><see cref="ContactFieldTypes"/>.</summary>
    public string Type { get; set; } = ContactFieldTypes.Text;

    /// <summary>The choices of a <c>select</c> field.</summary>
    public List<string> Options { get; set; } = [];

    public int SortOrder { get; set; }

    public static ContactFieldDefinition Create(Guid workspaceId, string key, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Key = key,
        CreatedAt = now,
    };
}

public static class ContactFieldTypes
{
    public const string Text = "text";
    public const string Number = "number";

    /// <summary>A Gregorian ISO date (yyyy-MM-dd); the panel shows and edits it in the Jalali calendar.</summary>
    public const string Date = "date";
    public const string Boolean = "boolean";
    public const string Email = "email";

    /// <summary>Stored as E.164.</summary>
    public const string Phone = "phone";
    public const string Select = "select";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Text, Number, Date, Boolean, Email, Phone, Select };
}

/// <summary>A saved filter over contacts. <see cref="RuleJson"/> is a <c>SegmentRule</c>, evaluated to SQL when it is used.</summary>
public sealed class ContactSegment : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public string Name { get; set; } = string.Empty;

    public string RuleJson { get; set; } = "{}";

    public Guid CreatedBy { get; init; }

    public static ContactSegment Create(Guid workspaceId, string name, string ruleJson, Guid createdBy, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Name = name,
        RuleJson = ruleJson,
        CreatedBy = createdBy,
        CreatedAt = now,
    };
}
