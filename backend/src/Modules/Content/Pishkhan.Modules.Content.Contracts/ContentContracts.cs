using Pishkhan.Contracts;

namespace Pishkhan.Modules.Content.Contracts;

/// <summary>Website form kinds: the pre-launch waitlist and the contact (sales) form.</summary>
public static class LeadTypes
{
    public const string Waitlist = "waitlist";
    public const string Contact = "contact";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Waitlist, Contact };
}

/// <summary>
/// Content the public website shows changed (a plan or a CMS entry was published). The Content module calls the website's signed
/// revalidation webhook with these Next.js cache <see cref="Tags"/> and <see cref="Paths"/>. Keyed by the first tag or path.
/// </summary>
[IntegrationEvent(Topics.SiteRevalidationRequested)]
public sealed record SiteRevalidationRequested(IReadOnlyList<string> Tags, IReadOnlyList<string> Paths) : IIntegrationEvent
{
    public string PartitionKey() => Tags.FirstOrDefault() ?? Paths.FirstOrDefault() ?? "site";
}

/// <summary>Cache tags of the public website (Next.js <c>revalidateTag</c>); one place so the backend and the site agree.</summary>
public static class SiteTags
{
    public const string Plans = "plans";
}
