using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Conversations.Domain;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Conversations.Services;

/// <summary>Who a campaign may text (BE-2.7 acceptance: only consented contacts). Pure, so it is tested on its own.</summary>
internal static class CampaignAudience
{
    /// <summary>The contact's mobile when its latest SMS consent decision is a grant and it has a valid number; otherwise null.</summary>
    public static PhoneNumber? Reachable(IReadOnlyDictionary<string, string> fields, bool? latestSmsConsent) =>
        latestSmsConsent == true && fields.TryGetValue(FieldKeys.Phone, out var raw) && PhoneNumber.TryParse(raw, out var phone) ? phone : null;
}

/// <summary>
/// Outbound group: one batch of a campaign. Inside the transaction it picks the next contacts, keeps those reachable, and claims them
/// (one row per contact and campaign, so a redelivered batch texts nobody twice). The texts go out after the commit; a text that fails is
/// recorded as failed and not retried.
/// </summary>
internal sealed class OutageCampaignBatchHandler(
    ConversationsDbContext db, IEntitlements entitlements, ISmsSender sms, IChannelDirectory channels, IBaleLinks links, IClock clock, ILogger<OutageCampaignBatchHandler> logger)
    : IdempotentHandler<OutageCampaignBatchRequested, ConversationsDbContext>(db)
{
    private readonly List<(Guid ContactId, PhoneNumber Phone, string Text)> _sends = [];
    private OutageCampaign? _campaign;

    protected override async Task HandleOnceAsync(OutageCampaignBatchRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var campaign = await Db.OutageCampaigns.FirstOrDefaultAsync(c => c.Id == message.CampaignId, cancellationToken);
        if (campaign is not { Status: CampaignStatuses.Sending })
        {
            return;
        }

        var now = clock.UtcNow;
        var page = Db.Contacts.AsNoTracking();
        if (message.AfterCreatedAt is { } at && message.AfterContactId is { } id)
        {
            page = page.Where(c => EF.Functions.GreaterThan(ValueTuple.Create(c.CreatedAt, c.Id), ValueTuple.Create(at, id)));
        }

        var contacts = await page
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .Take(OutageCampaign.BatchSize)
            .Select(c => new
            {
                c.Id,
                c.CreatedAt,
                c.Fields,
                Consent = Db.Consents.Where(k => k.ContactId == c.Id && k.Channel == ConsentChannels.Sms)
                    .OrderByDescending(k => k.CreatedAt).ThenByDescending(k => k.Id).Select(k => (bool?)k.Granted).FirstOrDefault(),
            })
            .ToListAsync(cancellationToken);

        var ids = contacts.Select(c => c.Id).ToList();
        var already = (await Db.OutageCampaignRecipients.Where(r => r.CampaignId == campaign.Id && ids.Contains(r.ContactId)).Select(r => r.ContactId).ToListAsync(cancellationToken))
            .ToHashSet();

        var limit = (await entitlements.GetAsync(campaign.WorkspaceId, cancellationToken)).Limits.GetValueOrDefault(BillingMeters.Sms);
        var used = limit is null ? 0 : await entitlements.GetUsageAsync(campaign.WorkspaceId, BillingMeters.Sms, cancellationToken);
        var allowance = limit is { } l ? Math.Max(0, l - used) : long.MaxValue;

        // {link}: each recipient's own link to the Bale bot, so their Bale chat joins their contact. Without a bot the placeholder is removed.
        var bale = campaign.Text.Contains(OutageCampaign.LinkPlaceholder, StringComparison.Ordinal)
            ? await Features.OutageCampaignEndpoints.BaleBotAsync(channels, cancellationToken)
            : null;
        var limitReached = false;
        foreach (var c in contacts.Where(c => !already.Contains(c.Id)))
        {
            if (CampaignAudience.Reachable(c.Fields, c.Consent) is not { } phone)
            {
                continue;
            }

            if (_sends.Count >= allowance)
            {
                limitReached = true;
                break;
            }

            Db.OutageCampaignRecipients.Add(new OutageCampaignRecipient
            {
                WorkspaceId = campaign.WorkspaceId, CampaignId = campaign.Id, ContactId = c.Id, Status = RecipientStatuses.Sent, CreatedAt = now,
            });
            var text = campaign.Text;
            if (text.Contains(OutageCampaign.LinkPlaceholder, StringComparison.Ordinal))
            {
                var link = string.Empty;
                if (bale is { Username: { } username })
                {
                    var token = ContactLinkToken.Create(campaign.WorkspaceId, c.Id, bale.Id, now);
                    Db.ContactLinkTokens.Add(token);
                    link = links.DeepLink(username, token.Token);
                }

                text = text.Replace(OutageCampaign.LinkPlaceholder, link, StringComparison.Ordinal);
            }

            _sends.Add((c.Id, phone, text));
        }

        campaign.Batch(contacts.Count, _sends.Count, 0, now);
        if (limitReached)
        {
            campaign.Finish(CampaignStatuses.LimitReached, now);
        }
        else if (contacts.Count == OutageCampaign.BatchSize)
        {
            Db.Publish(
                new OutageCampaignBatchRequested(campaign.WorkspaceId, campaign.Id, contacts[^1].CreatedAt, contacts[^1].Id), campaign.Id.ToString(), workspaceId: campaign.WorkspaceId);
        }
        else
        {
            campaign.Finish(CampaignStatuses.Completed, now);
        }

        if (_sends.Count > 0)
        {
            Db.Publish(new UsageRecorded(campaign.WorkspaceId, BillingMeters.Sms, _sends.Count, now, campaign.Id), campaign.WorkspaceId.ToString(), workspaceId: campaign.WorkspaceId);
        }

        _campaign = campaign;
    }

    protected override async Task AfterCommitAsync(OutageCampaignBatchRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_campaign is null || _sends.Count == 0)
        {
            return;
        }

        var failed = new List<Guid>();
        foreach (var (contactId, phone, text) in _sends)
        {
            try
            {
                await sms.SendAsync(new SmsMessage(phone, text, Purpose: SmsPurposes.Campaign, WorkspaceId: _campaign.WorkspaceId), cancellationToken);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException && !cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning("Campaign {CampaignId}: SMS to contact {ContactId} failed: {Error}", _campaign.Id, contactId, e.Message);
                failed.Add(contactId);
            }
        }

        if (failed.Count == 0)
        {
            return;
        }

        // Outside the batch's transaction: the texts already went (or did not); this only corrects the counts.
        Db.ChangeTracker.Clear();
        await Db.OutageCampaignRecipients.Where(r => r.CampaignId == _campaign.Id && failed.Contains(r.ContactId))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, RecipientStatuses.Failed), cancellationToken);
        var count = failed.Count;
        await Db.OutageCampaigns.Where(c => c.Id == _campaign.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(c => c.Sent, c => c.Sent - count).SetProperty(c => c.Failed, c => c.Failed + count), cancellationToken);
    }
}
