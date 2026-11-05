using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Billing.Domain;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Services;

/// <summary>
/// The workspace's wallet within the current unit of work: loads it (or opens an empty one), gives back the wallet part of checkouts that
/// were left unpaid, and records changes. The caller saves; the wallet row's version makes concurrent changes fail rather than overspend.
/// </summary>
internal sealed class WalletLedger(BillingDbContext db, IClock clock)
{
    /// <summary>
    /// A checkout's wallet part is held this long when the payer never comes back from Zarinpal (well past Zarinpal's own payment timeout,
    /// so a late payment still finds it). A payer who leaves the gateway gets it back at once.
    /// </summary>
    public static readonly TimeSpan HoldTimeout = Checkout.Lifetime * 2;

    /// <summary>The workspace's wallet, tracked; an empty one is added when it has none yet. Call within the workspace's tenant scope.</summary>
    public async Task<Wallet> GetAsync(Guid workspaceId, CancellationToken ct)
    {
        var wallet = await db.Wallets.FirstOrDefaultAsync(w => w.WorkspaceId == workspaceId, ct);
        if (wallet is null)
        {
            wallet = db.Wallets.Local.FirstOrDefault(w => w.WorkspaceId == workspaceId) ?? Wallet.Open(workspaceId, clock.UtcNow);
            if (db.Entry(wallet).State == EntityState.Detached)
            {
                db.Wallets.Add(wallet);
            }
        }

        return wallet;
    }

    /// <summary>The wallet after giving back holds of checkouts left unpaid past <see cref="HoldTimeout"/>.</summary>
    public async Task<Wallet> SettledAsync(Guid workspaceId, CancellationToken ct)
    {
        var wallet = await GetAsync(workspaceId, ct);
        var stale = clock.UtcNow - HoldTimeout;
        var held = await db.Checkouts
            .Where(c => c.WorkspaceId == workspaceId && c.Status == CheckoutStatuses.Pending && c.WalletUsed > 0 && !c.WalletReleased && c.CreatedAt < stale)
            .ToListAsync(ct);
        foreach (var checkout in held)
        {
            GiveBack(wallet, checkout, "checkout_expired");
        }

        return wallet;
    }

    /// <summary>Takes a new checkout's wallet part.</summary>
    public void Take(Wallet wallet, Checkout checkout)
    {
        if (checkout.WalletUsed > 0)
        {
            Add(wallet.Change(-checkout.WalletUsed, WalletEntryKinds.Checkout, null, checkout.Id, WalletActors.User, checkout.CreatedBy, clock.UtcNow));
        }
    }

    /// <summary>Gives back a checkout's wallet part once (a failed or expired checkout, or a refund that ended what it bought).</summary>
    public long GiveBack(Wallet wallet, Checkout checkout, string reason, string actorKind = WalletActors.System, Guid? actorId = null)
    {
        if (checkout.WalletUsed <= 0 || checkout.WalletReleased)
        {
            return 0;
        }

        var now = clock.UtcNow;
        Add(wallet.Change(checkout.WalletUsed, WalletEntryKinds.Released, reason, checkout.Id, actorKind, actorId, now));
        checkout.WalletGivenBack(now);
        return checkout.WalletUsed;
    }

    public WalletEntry Change(Wallet wallet, long amount, string kind, string? reason, Guid? checkoutId, string actorKind, Guid? actorId)
    {
        var entry = wallet.Change(amount, kind, reason, checkoutId, actorKind, actorId, clock.UtcNow);
        Add(entry);
        return entry;
    }

    private void Add(WalletEntry entry) => db.WalletEntries.Add(entry);
}
