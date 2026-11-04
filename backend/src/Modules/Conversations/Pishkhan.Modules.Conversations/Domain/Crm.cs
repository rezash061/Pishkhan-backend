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

/// <summary>A completed flow run's answers (DM forms are flow question nodes). One per run.</summary>
public sealed class FormSubmission : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid FlowId { get; init; }

    public int FlowVersion { get; init; }

    /// <summary>The flow's name when it was submitted.</summary>
    public string FlowName { get; init; } = string.Empty;

    public Guid RunId { get; init; }

    public Guid ConversationId { get; init; }

    public Guid ContactId { get; init; }

    public Dictionary<string, string> Answers { get; init; } = new(StringComparer.Ordinal);

    public DateTimeOffset SubmittedAt { get; init; }

    public static FormSubmission Create(
        Guid workspaceId, Guid flowId, int flowVersion, string flowName, Guid runId, Guid conversationId, Guid contactId, IReadOnlyDictionary<string, string> answers,
        DateTimeOffset submittedAt, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(submittedAt),
        WorkspaceId = workspaceId,
        FlowId = flowId,
        FlowVersion = flowVersion,
        FlowName = flowName,
        RunId = runId,
        ConversationId = conversationId,
        ContactId = contactId,
        Answers = new Dictionary<string, string>(answers, StringComparer.Ordinal),
        SubmittedAt = submittedAt,
        CreatedAt = now,
    };
}

/// <summary>Where a consent decision came from.</summary>
public static class ConsentSources
{
    public const string Manual = "manual";

    /// <summary>The customer agreed in a flow (BE-2.7 <c>capture_phone</c>); the evidence is the question they answered.</summary>
    public const string Flow = "flow";

    /// <summary>The customer started the shop's bot from a link (BE-2.7): choosing the channel is the consent.</summary>
    public const string Channel = "channel";
}

/// <summary>Contact fields with a meaning of their own.</summary>
public static class FieldKeys
{
    /// <summary>The customer's mobile, in national form (<c>09121234567</c>).</summary>
    public const string Phone = "phone";
}

public static class ConsentChannels
{
    /// <summary>Calls to the contact's phone.</summary>
    public const string Phone = "phone";
    public const string Sms = "sms";
    public const string Bale = "bale";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Phone, Sms, Bale };
}

/// <summary>
/// One consent decision of a contact for a channel; the latest per channel is in force. Kept as a history, never updated, so the
/// workspace can show when and how the contact agreed (or withdrew).
/// </summary>
public sealed class ContactConsent : Entity<Guid>, ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid ContactId { get; init; }

    /// <summary><see cref="ConsentChannels"/>.</summary>
    public string Channel { get; init; } = string.Empty;

    public bool Granted { get; init; }

    /// <summary><c>manual</c> (a team member), <c>flow</c> (a customer's answer) or <c>import</c>.</summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>What the customer was shown or said, or the member's note.</summary>
    public string? Evidence { get; init; }

    /// <summary>The team member who recorded it, for a manual record.</summary>
    public Guid? RecordedBy { get; init; }

    public static ContactConsent Create(Guid workspaceId, Guid contactId, string channel, bool granted, string source, string? evidence, Guid? recordedBy, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        ContactId = contactId,
        Channel = channel,
        Granted = granted,
        Source = source,
        Evidence = evidence,
        RecordedBy = recordedBy,
        CreatedAt = now,
    };
}

public static class ExportStatuses
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Failed = "failed";

    /// <summary>Downloaded once; the file is gone.</summary>
    public const string Downloaded = "downloaded";
    public const string Expired = "expired";
}

/// <summary>An Excel export of contacts, built in the background into the exports bucket and downloadable once by the member who asked.</summary>
public sealed class ContactExport : Entity<Guid>, ITenantOwned
{
    /// <summary>How long a ready file waits for its download.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(1);

    public Guid WorkspaceId { get; init; }

    public Guid RequestedBy { get; init; }

    /// <summary>The list filters it was asked with (q, tag, segmentId), as JSON.</summary>
    public string FilterJson { get; init; } = "{}";

    public string Status { get; set; } = ExportStatuses.Pending;

    public int? RowCount { get; set; }

    public string? StorageKey { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? DownloadedAt { get; set; }

    public static ContactExport Create(Guid workspaceId, Guid requestedBy, string filterJson, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        RequestedBy = requestedBy,
        FilterJson = filterJson,
        CreatedAt = now,
    };

    public string StatusAt(DateTimeOffset now) => Status == ExportStatuses.Ready && ExpiresAt <= now ? ExportStatuses.Expired : Status;
}
