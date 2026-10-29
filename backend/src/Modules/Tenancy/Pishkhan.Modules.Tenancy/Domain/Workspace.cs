using System.Text.RegularExpressions;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Tenancy.Domain;

public enum WorkspaceStatus
{
    Active,
    Suspended,
    PendingDeletion,
}

/// <summary>A tenant: one business. Owns channels, contacts, catalog and automations.</summary>
public sealed partial class Workspace : AggregateRoot<Guid>
{
    private Workspace()
    {
    }

    public Guid? OrganizationId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    /// <summary>Launch verticals (assumption A5): clothing, cosmetics, jewelry, beauty_clinic, courses, other.</summary>
    public string Industry { get; private set; } = "other";

    public string TimeZone { get; private set; } = "Asia/Tehran";

    public Guid? PlanId { get; private set; }

    public WorkspaceStatus Status { get; private set; }

    public DateTimeOffset? DeletionRequestedAt { get; private set; }

    public BusinessHours BusinessHours { get; private set; } = BusinessHours.Default;

    public static readonly IReadOnlyList<string> Industries = ["clothing", "cosmetics", "jewelry", "beauty_clinic", "courses", "other"];

    public static Workspace Create(string name, string slug, string industry, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        Name = name.Trim(),
        Slug = slug,
        Industry = industry,
        Status = WorkspaceStatus.Active,
        CreatedAt = now,
    };

    public void Update(string? name, string? industry)
    {
        if (name is not null)
        {
            Name = name.Trim();
        }

        if (industry is not null)
        {
            Industry = industry;
        }
    }

    public void SetBusinessHours(BusinessHours hours) => BusinessHours = hours;

    public void Suspend() => Status = WorkspaceStatus.Suspended;

    /// <summary>Deletion has a 30-day grace period (backend plan 3.4, data retention).</summary>
    public void RequestDeletion(DateTimeOffset now)
    {
        Status = WorkspaceStatus.PendingDeletion;
        DeletionRequestedAt = now;
    }

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38}[a-z0-9])$", RegexOptions.CultureInvariant, 100)]
    public static partial Regex SlugPattern();

    /// <summary>A URL-safe slug from a Latin name, or a random one for Persian names.</summary>
    public static string SuggestSlug(string name)
    {
        var latin = new string(name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        while (latin.Contains("--", StringComparison.Ordinal))
        {
            latin = latin.Replace("--", "-", StringComparison.Ordinal);
        }

        var suffix = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(3));
        return latin.Length >= 3 ? $"{latin[..Math.Min(latin.Length, 30)].TrimEnd('-')}-{suffix}" : "shop-" + suffix;
    }
}
