using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Automation.Contracts;
using Pishkhan.Modules.Knowledge.Contracts;
using Pishkhan.Modules.Templates.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Templates.Domain;

public static class TemplateStatuses
{
    public const string Draft = "draft";
    public const string Published = "published";
    public const string Archived = "archived";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Draft, Published, Archived };
}

/// <summary>
/// What a template adds to a workspace: required facts (for the knowledge score), FAQs (added switched off, for the owner to fill in and
/// switch on), flows (published) and rules (switched off) in the panel API's JSON shapes, and the AI agent's starting profile.
/// </summary>
public sealed record TemplateContent(
    IReadOnlyList<RequiredFact> RequiredFacts,
    IReadOnlyList<TemplateFaq> Faqs,
    IReadOnlyList<TemplateFlow> Flows,
    IReadOnlyList<TemplateRule> Rules,
    TemplateAgentProfile? AgentProfile)
{
    public static TemplateContent Empty { get; } = new([], [], [], [], null);
}

/// <summary>
/// One version of an industry's template (backend plan 9.19, gap G32). Like plans, every version is a row: one draft and one published
/// version per industry at a time; publishing a draft archives the version it replaces.
/// </summary>
public sealed class IndustryTemplate : AggregateRoot<Guid>
{
    public const int MaxName = 100;
    public const int MaxDescription = 500;

    private IndustryTemplate()
    {
    }

    /// <summary>One of the workspace industries: clothing, cosmetics, jewelry, beauty_clinic, courses or other.</summary>
    public string Industry { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public string Status { get; private set; } = TemplateStatuses.Draft;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public TemplateContent Content { get; private set; } = TemplateContent.Empty;

    /// <summary>The staff member who last changed it; null for the built-in templates.</summary>
    public Guid? UpdatedBy { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? PublishedBy { get; private set; }

    public static IndustryTemplate Create(string industry, string name, string? description, TemplateContent content, Guid? staffId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        Industry = industry,
        Version = 1,
        Name = name.Trim(),
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim(),
        Content = content,
        UpdatedBy = staffId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Update(string name, string? description, TemplateContent content, Guid? staffId, DateTimeOffset now)
    {
        if (Status != TemplateStatuses.Draft)
        {
            throw new DomainException("templates.not_draft");
        }

        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Content = content;
        UpdatedBy = staffId;
        UpdatedAt = now;
    }

    /// <summary>A new draft copying this version's content.</summary>
    public IndustryTemplate NextVersion(int version, Guid? staffId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        Industry = Industry,
        Version = version,
        Name = Name,
        Description = Description,
        Content = Content,
        UpdatedBy = staffId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Publish(Guid? staffId, DateTimeOffset now)
    {
        if (Status != TemplateStatuses.Draft)
        {
            throw new DomainException("templates.not_draft");
        }

        Status = TemplateStatuses.Published;
        PublishedAt = now;
        PublishedBy = staffId;
        UpdatedBy = staffId;
        UpdatedAt = now;
    }

    public void Archive(Guid? staffId, DateTimeOffset now)
    {
        Status = TemplateStatuses.Archived;
        UpdatedBy = staffId;
        UpdatedAt = now;
    }
}

/// <summary>A template applied to a workspace, with what it added. Applying again adds only what the workspace never had.</summary>
public sealed class TemplateApplication : Entity<Guid>, ITenantOwned
{
    private TemplateApplication()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid TemplateId { get; private set; }

    public string Industry { get; private set; } = string.Empty;

    public int Version { get; private set; }

    public Guid? AppliedBy { get; private set; }

    public int FaqsAdded { get; private set; }

    public int FlowsAdded { get; private set; }

    public int RulesAdded { get; private set; }

    /// <summary>False when the workspace already had its own agent profile, which is never replaced.</summary>
    public bool ProfileApplied { get; private set; }

    /// <summary>Items the workspace already had (or had deleted), and rules whose flow it deleted.</summary>
    public int Skipped { get; private set; }

    public static TemplateApplication Record(
        Guid workspaceId, IndustryTemplate template, Guid? userId, int faqsAdded, int flowsAdded, int rulesAdded, bool profileApplied, int skipped, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        TemplateId = template.Id,
        Industry = template.Industry,
        Version = template.Version,
        AppliedBy = userId,
        FaqsAdded = faqsAdded,
        FlowsAdded = flowsAdded,
        RulesAdded = rulesAdded,
        ProfileApplied = profileApplied,
        Skipped = skipped,
        CreatedAt = now,
        UpdatedAt = now,
    };
}
