using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

public enum PlanStatus
{
    /// <summary>Being edited in the back office; never shown or sold.</summary>
    Draft,

    /// <summary>The version on sale for its code (at most one per code).</summary>
    Published,

    /// <summary>Replaced or withdrawn. Subscriptions on it keep it until renewal.</summary>
    Archived,
}

/// <summary>One line of the pricing card, for example «بررسی رسید کارت‌به‌کارت» with limit «۵۰۰ رسید».</summary>
public sealed record PlanFeature(string Key, string Label, bool Included, string? Limit);

/// <summary>
/// One version of a plan (admin plan 4.4). A plan is its <see cref="Code"/>; editing a published plan creates a new draft version,
/// and publishing it archives the previous one, so existing subscriptions keep the version they bought. Prices are in Rial.
/// </summary>
public sealed class Plan : AggregateRoot<Guid>
{
    private Plan()
    {
    }

    public string Code { get; private set; } = string.Empty;

    public int VersionNumber { get; private set; }

    public PlanStatus Status { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? Tagline { get; private set; }

    public string? Description { get; private set; }

    public long MonthlyPrice { get; private set; }

    /// <summary>Null when the plan is not sold yearly.</summary>
    public long? AnnualPrice { get; private set; }

    /// <summary>Price of one AI conversation over the limit (soft overage, Phase 2); null when overage is not offered.</summary>
    public long? OveragePerConversation { get; private set; }

    /// <summary>Limits per meter (<c>BillingMeters</c>); null values are unlimited.</summary>
    public Dictionary<string, int?> Limits { get; private set; } = [];

    public List<PlanFeature> Features { get; private set; } = [];

    /// <summary>Listed on the website. Private plans are granted by staff only.</summary>
    public bool IsPublic { get; private set; }

    /// <summary>The free tier: what a workspace without a subscription gets.</summary>
    public bool IsFree { get; private set; }

    public bool Highlighted { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static Plan Create(string code, PlanDraft draft, Guid staffId, DateTimeOffset now)
    {
        var plan = new Plan { Id = Uuid.NewV7(now), Code = code, VersionNumber = 1, Status = PlanStatus.Draft, CreatedAt = now };
        plan.Apply(draft, staffId);
        return plan;
    }

    /// <summary>A new draft version with this version's content.</summary>
    public Plan NextVersion(int version, Guid staffId, DateTimeOffset now)
    {
        var next = new Plan { Id = Uuid.NewV7(now), Code = Code, VersionNumber = version, Status = PlanStatus.Draft, CreatedAt = now };
        next.Apply(ToDraft(), staffId);
        return next;
    }

    public void Update(PlanDraft draft, Guid staffId)
    {
        if (Status != PlanStatus.Draft)
        {
            throw new DomainException("billing.plan.not_draft");
        }

        Apply(draft, staffId);
    }

    public void Publish(Guid staffId, DateTimeOffset now)
    {
        if (Status != PlanStatus.Draft)
        {
            throw new DomainException("billing.plan.not_draft");
        }

        Status = PlanStatus.Published;
        PublishedAt = now;
        UpdatedBy = staffId;
    }

    public void Archive(Guid staffId)
    {
        Status = PlanStatus.Archived;
        UpdatedBy = staffId;
    }

    public PlanDraft ToDraft() =>
        new(Name, Tagline, Description, MonthlyPrice, AnnualPrice, OveragePerConversation, new Dictionary<string, int?>(Limits), [.. Features], IsPublic, IsFree, Highlighted, SortOrder);

    public int? Limit(string meter) => Limits.GetValueOrDefault(meter);

    /// <summary>The API spelling: draft, published, archived.</summary>
    public static string StatusName(PlanStatus status) => status.ToString().ToLowerInvariant();

    private void Apply(PlanDraft draft, Guid staffId)
    {
        Name = draft.Name.Trim();
        Tagline = Clean(draft.Tagline);
        Description = Clean(draft.Description);
        MonthlyPrice = draft.MonthlyPrice;
        AnnualPrice = draft.AnnualPrice;
        OveragePerConversation = draft.OveragePerConversation;
        Limits = new Dictionary<string, int?>(draft.Limits, StringComparer.Ordinal);
        Features = [.. draft.Features.Select(f => f with { Key = f.Key.Trim(), Label = f.Label.Trim(), Limit = Clean(f.Limit) })];
        IsPublic = draft.IsPublic;
        IsFree = draft.IsFree;
        Highlighted = draft.Highlighted;
        SortOrder = draft.SortOrder;
        UpdatedBy = staffId;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>The editable content of a plan version.</summary>
public sealed record PlanDraft(
    string Name, string? Tagline, string? Description, long MonthlyPrice, long? AnnualPrice, long? OveragePerConversation,
    IReadOnlyDictionary<string, int?> Limits, IReadOnlyList<PlanFeature> Features, bool IsPublic, bool IsFree, bool Highlighted, int SortOrder);
