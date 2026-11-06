using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

public static class WalletEntryKinds
{
    /// <summary>The seller paid into the wallet through Zarinpal.</summary>
    public const string TopUp = "top_up";

    /// <summary>Finance added or took off credit (compensation, correction), with a reason.</summary>
    public const string Adjustment = "adjustment";

    /// <summary>A refund paid into the wallet instead of back through Zarinpal.</summary>
    public const string Refund = "refund";

    /// <summary>Spent on a checkout, taken when the checkout is opened.</summary>
    public const string Checkout = "checkout";

    /// <summary>Given back: the checkout failed or expired, or a refund ended what it bought.</summary>
    public const string Released = "released";
}

/// <summary>
/// The workspace's prepaid credit in Rial (admin plan 4.4). Spent on subscription checkouts; topped up by the seller through Zarinpal or by
/// Finance. Never negative. Every change is a <see cref="WalletEntry"/> with the balance after it.
/// </summary>
public sealed class Wallet : AggregateRoot<Guid>, ITenantOwned
{
    private Wallet()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public long Balance { get; private set; }

    public static Wallet Open(Guid workspaceId, DateTimeOffset now) => new() { Id = workspaceId, WorkspaceId = workspaceId, CreatedAt = now, UpdatedAt = now };

    /// <summary>Adds (positive) or takes off (negative) <paramref name="amount"/>; throws <c>billing.wallet.insufficient</c> below zero.</summary>
    public WalletEntry Change(long amount, string kind, string? reason, Guid? checkoutId, string actorKind, Guid? actorId, DateTimeOffset now)
    {
        if (amount == 0)
        {
            throw new DomainException("billing.wallet.zero");
        }

        if (Balance + amount < 0)
        {
            throw new DomainException("billing.wallet.insufficient");
        }

        Balance += amount;
        UpdatedAt = now;
        return WalletEntry.Create(this, amount, kind, reason, checkoutId, actorKind, actorId, now);
    }
}

public static class WalletActors
{
    public const string User = "user";
    public const string Staff = "staff";
    public const string System = "system";
}

/// <summary>One change to a wallet. <see cref="Amount"/> is signed Rial.</summary>
public sealed class WalletEntry : ITenantOwned
{
    public const int MaxReason = 300;

    private WalletEntry()
    {
    }

    public Guid Id { get; private init; }

    public Guid WorkspaceId { get; private init; }

    public long Amount { get; private init; }

    public long BalanceAfter { get; private init; }

    public string Kind { get; private init; } = WalletEntryKinds.Adjustment;

    public string? Reason { get; private init; }

    public Guid? CheckoutId { get; private init; }

    public string ActorKind { get; private init; } = WalletActors.System;

    public Guid? ActorId { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    internal static WalletEntry Create(Wallet wallet, long amount, string kind, string? reason, Guid? checkoutId, string actorKind, Guid? actorId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = wallet.WorkspaceId,
        Amount = amount,
        BalanceAfter = wallet.Balance,
        Kind = kind,
        Reason = reason,
        CheckoutId = checkoutId,
        ActorKind = actorKind,
        ActorId = actorId,
        CreatedAt = now,
    };
}
