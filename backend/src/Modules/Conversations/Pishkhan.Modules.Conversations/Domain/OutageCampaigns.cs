using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Domain;

public static class CampaignStatuses
{
    public const string Sending = "sending";
    public const string Completed = "completed";

    /// <summary>Stopped because the plan's SMS allowance for the period ran out; recipients sent so far stay sent.</summary>
    public const string LimitReached = "limit_reached";
}

public static class RecipientStatuses
{
    public const string Sent = "sent";
    public const string Failed = "failed";
}

/// <summary>
/// "Move my customers" (BE-2.7, gap G11): one SMS to every contact whose latest SMS consent is a grant and who has a mobile number,
/// inviting them to continue on another channel. Sent in batches; a contact is texted at most once per campaign.
/// </summary>
public sealed class OutageCampaign : Entity<Guid>, ITenantOwned
{
    public const int MaxText = 300;
    public const int BatchSize = 100;

    /// <summary>Replaced in each recipient's text by their own link to the shop's Bale bot.</summary>
    public const string LinkPlaceholder = "{link}";

    /// <summary>One campaign per workspace per this many hours: it is an outage tool, not bulk marketing.</summary>
    public const int CooldownHours = 24;

    public Guid WorkspaceId { get; init; }

    /// <summary>sms (Bale comes with its adapter).</summary>
    public string Channel { get; init; } = "sms";

    /// <summary>What every recipient gets, the shop's name in front.</summary>
    public required string Text { get; init; }

    public string Status { get; private set; } = CampaignStatuses.Sending;

    /// <summary>Contacts considered so far (with or without a number and consent).</summary>
    public int Scanned { get; private set; }

    public int Sent { get; private set; }

    public int Failed { get; private set; }

    public Guid? CreatedBy { get; init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static OutageCampaign Start(Guid workspaceId, string text, Guid? userId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Text = text,
        CreatedBy = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Batch(int scanned, int sent, int failed, DateTimeOffset now)
    {
        Scanned += scanned;
        Sent += sent;
        Failed += failed;
        UpdatedAt = now;
    }

    public void Finish(string status, DateTimeOffset now)
    {
        Status = status;
        CompletedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>A contact a campaign texted; unique per campaign, so a retried batch never texts anyone twice. The number is not stored.</summary>
public sealed class OutageCampaignRecipient : ITenantOwned
{
    public Guid WorkspaceId { get; init; }

    public Guid CampaignId { get; init; }

    public Guid ContactId { get; init; }

    public required string Status { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
