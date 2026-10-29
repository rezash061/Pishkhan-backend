namespace Pishkhan.Modules.Content.Contracts;

/// <summary>Website form kinds: the pre-launch waitlist and the contact (sales) form.</summary>
public static class LeadTypes
{
    public const string Waitlist = "waitlist";
    public const string Contact = "contact";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Waitlist, Contact };
}
