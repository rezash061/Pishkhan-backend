using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Content.Domain;

public sealed record LeadDetails(string? Name, string? InstagramHandle, string? Vertical, string? MonthlyDms, string? Message, IReadOnlyDictionary<string, string> Utm);

/// <summary>
/// A waitlist or contact submission from the website. The phone is encrypted, with a blind index so a repeat submission
/// (same phone and type within a day) updates the lead instead of adding a duplicate.
/// </summary>
public sealed class Lead : Entity<Guid>
{
    public static readonly TimeSpan MergeWindow = TimeSpan.FromDays(1);

    private Lead()
    {
    }

    public string Type { get; private set; } = string.Empty;

    public string PhoneEnc { get; private set; } = string.Empty;

    public string PhoneBidx { get; private set; } = string.Empty;

    public string? Name { get; private set; }

    public string? InstagramHandle { get; private set; }

    public string? Vertical { get; private set; }

    public string? MonthlyDms { get; private set; }

    public string? Message { get; private set; }

    public Dictionary<string, string> Utm { get; private set; } = [];

    public string? Ip { get; private set; }

    /// <summary>How many times this person submitted the form within the merge window (1 for a new lead).</summary>
    public int Submissions { get; private set; }

    public static Lead Submit(string type, string phoneEnc, string phoneBidx, LeadDetails details, string? ip, DateTimeOffset now)
    {
        var lead = new Lead { Id = Uuid.NewV7(now), Type = type, PhoneEnc = phoneEnc, PhoneBidx = phoneBidx, CreatedAt = now };
        lead.Apply(details, ip, now);
        return lead;
    }

    /// <summary>A repeat submission: newer non-empty answers win, earlier ones are kept; UTM keys are merged.</summary>
    public void Resubmit(LeadDetails details, string? ip, DateTimeOffset now) => Apply(details, ip, now);

    private void Apply(LeadDetails d, string? ip, DateTimeOffset now)
    {
        Name = d.Name ?? Name;
        InstagramHandle = d.InstagramHandle ?? InstagramHandle;
        Vertical = d.Vertical ?? Vertical;
        MonthlyDms = d.MonthlyDms ?? MonthlyDms;
        Message = d.Message ?? Message;
        Utm = new Dictionary<string, string>(Utm, StringComparer.Ordinal);
        foreach (var (key, value) in d.Utm)
        {
            Utm[key] = value;
        }

        Ip = ip ?? Ip;
        Submissions++;
        UpdatedAt = now;
    }
}
