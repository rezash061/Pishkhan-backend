using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Billing.Domain;

/// <summary>
/// Money paid back for a paid checkout (admin plan 4.4). The transfer itself is made in Zarinpal's panel; this records it, with Zarinpal's
/// reference when staff have one. The refunds of a checkout never add up to more than it was paid.
/// </summary>
public sealed class Refund : ITenantOwned
{
    private Refund()
    {
    }

    public Guid Id { get; private set; }

    public Guid WorkspaceId { get; private set; }

    public Guid CheckoutId { get; private set; }

    /// <summary>Rial.</summary>
    public long Amount { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string? ReferenceId { get; private set; }

    /// <summary>The subscription the checkout bought was ended with the refund.</summary>
    public bool EndedSubscription { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Validates against what the checkout was paid and what was already refunded; throws <see cref="DomainException"/>.</summary>
    public static Refund Create(Checkout checkout, long alreadyRefunded, long amount, string reason, string? referenceId, bool endSubscription, Guid staffId, DateTimeOffset now)
    {
        if (checkout.Status != CheckoutStatuses.Paid)
        {
            throw new DomainException("billing.refund.not_paid");
        }

        if (amount <= 0 || amount > checkout.Amount - alreadyRefunded)
        {
            throw new DomainException("billing.refund.too_much");
        }

        return new Refund
        {
            Id = Uuid.NewV7(now),
            WorkspaceId = checkout.WorkspaceId,
            CheckoutId = checkout.Id,
            Amount = amount,
            Reason = reason.Trim(),
            ReferenceId = string.IsNullOrWhiteSpace(referenceId) ? null : referenceId.Trim(),
            EndedSubscription = endSubscription,
            CreatedBy = staffId,
            CreatedAt = now,
        };
    }
}
