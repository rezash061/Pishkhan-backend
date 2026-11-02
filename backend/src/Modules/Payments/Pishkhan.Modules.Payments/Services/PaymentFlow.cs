using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Payments.Services;

/// <summary>Merchant IDs at rest (backend plan 3.4: PSP keys are envelope-encrypted).</summary>
internal sealed class MerchantSecrets(ISecretProtector protector)
{
    private const string Purpose = "payments.merchant";

    public (string Enc, string Hint) Protect(string merchantId) => (protector.Protect(merchantId, Purpose), merchantId[^4..]);

    public GatewayMerchant Reveal(MerchantAccount account) => new(protector.Unprotect(account.MerchantIdEnc, Purpose), account.Sandbox);
}

/// <summary>Where the customer is sent next: the provider's page, or back to our payment page (with a message when it failed).</summary>
internal sealed record StartResult(Uri? Redirect, string? Error);

/// <summary>
/// The customer's side of a payment link: start a payment at the provider, then verify the callback. The amount sent and verified is
/// always the link's own; the callback only says which attempt it is (the authority) and whether the customer finished.
/// </summary>
internal sealed class PaymentFlow(
    PaymentsDbContext db, IEnumerable<IPaymentGateway> gateways, MerchantSecrets secrets, ITenantContext tenant, IOptions<PaymentsOptions> options, IClock clock)
{
    public async Task<PaymentLink?> FindByTokenAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64)
        {
            return null;
        }

        using (tenant.BeginSystemScope())
        {
            var link = await db.Links.AsNoTracking().FirstOrDefaultAsync(l => l.Token == token, ct);
            if (link is not null)
            {
                tenant.SetWorkspace(link.WorkspaceId);
            }

            return link;
        }
    }

    public async Task<StartResult> StartAsync(string token, CancellationToken ct)
    {
        var found = await FindByTokenAsync(token, ct);
        var now = clock.UtcNow;
        if (found is null)
        {
            return new StartResult(null, "not_found");
        }

        var link = await db.Links.FirstAsync(l => l.Id == found.Id, ct);
        if (!link.CanStart(now))
        {
            return new StartResult(null, link.Attempts >= PaymentLink.MaxAttempts ? "too_many_attempts" : "inactive");
        }

        var account = await db.MerchantAccounts.FirstOrDefaultAsync(ct);
        var gateway = gateways.FirstOrDefault(g => g.Provider == link.Provider);
        if (account is not { Enabled: true } || account.Provider != link.Provider || gateway is null)
        {
            return new StartResult(null, "merchant_unavailable");
        }

        var callback = new Uri($"{options.Value.PublicBaseUrl.TrimEnd('/')}/callback/{link.Provider}");
        var description = $"سفارش {link.OrderNumber}" + (link.ShopName is { Length: > 0 } shop ? $" - {shop}" : string.Empty);
        var started = await gateway.StartAsync(secrets.Reveal(account), link.Amount, callback, description, ct);
        link.Started(now);
        if (!started.Ok)
        {
            link.Failed(started.Error!, now);
            await db.SaveChangesAsync(ct);
            return new StartResult(null, "gateway_unavailable");
        }

        db.Attempts.Add(PaymentAttempt.Start(link, started.Authority!, now));
        await db.SaveChangesAsync(ct);
        return new StartResult(started.RedirectUrl, null);
    }

    /// <summary>
    /// The provider's callback. Returns the link's token (to show its page) or null for an unknown authority. A paid link stays paid: a
    /// repeated or late callback verifies nothing again. Only a verification for the link's own amount pays.
    /// </summary>
    public async Task<string?> CompleteAsync(string provider, string? authority, bool customerFinished, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(authority) || authority.Length > 64)
        {
            return null;
        }

        PaymentAttempt? attempt;
        using (tenant.BeginSystemScope())
        {
            attempt = await db.Attempts.FirstOrDefaultAsync(a => a.Authority == authority, ct);
        }

        if (attempt is null)
        {
            return null;
        }

        tenant.SetWorkspace(attempt.WorkspaceId);
        var link = await db.Links.FirstAsync(l => l.Id == attempt.LinkId, ct);
        if (link.Provider != provider)
        {
            return null;
        }

        var now = clock.UtcNow;
        if (attempt.Status != PaymentAttemptStatuses.Started || link.Status == PaymentLinkStatuses.Paid)
        {
            return link.Token;
        }

        if (!customerFinished)
        {
            attempt.Complete(false, "cancelled", now);
            link.Failed("cancelled", now);
            await db.SaveChangesAsync(ct);
            return link.Token;
        }

        var account = await db.MerchantAccounts.FirstOrDefaultAsync(ct);
        var gateway = gateways.FirstOrDefault(g => g.Provider == link.Provider);
        if (account is null || gateway is null)
        {
            return link.Token;
        }

        var verified = await gateway.VerifyAsync(secrets.Reveal(account), link.Amount, authority, ct);
        if (!verified.Ok)
        {
            // "unavailable" leaves the attempt open: the customer can come back through the same callback once Zarinpal answers.
            if (verified.Code != "unavailable")
            {
                attempt.Complete(false, verified.Code, now);
            }

            link.Failed(verified.Code, now);
            await SaveAsync(ct);
            return link.Token;
        }

        attempt.Complete(true, verified.Code, now);
        if (link.MarkPaid(verified.ReferenceId!, verified.CardMask, now))
        {
            db.Publish(
                new PaymentConfirmed(link.WorkspaceId, link.OrderId, link.Id, link.Provider, Money.Rial(link.Amount), verified.ReferenceId!, verified.CardMask, now),
                link.OrderId.ToString(), link.WorkspaceId);
            var entry = AuditEntries.Create(
                AuditActor.System, "payment.confirmed", "payment_link", link.Id.ToString(), link.WorkspaceId, now,
                after: new { orderId = link.OrderId, amount = link.Amount, provider = link.Provider, referenceId = verified.ReferenceId });
            db.Publish(entry, entry.PartitionKey(), link.WorkspaceId);
        }

        await SaveAsync(ct);
        return link.Token;
    }

    /// <summary>Two callbacks for one link at once: the second loses on the row version and the first one's result stands.</summary>
    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
        }
    }
}
