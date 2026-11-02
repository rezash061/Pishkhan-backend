using Pishkhan.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Payments.Contracts;

public static class PaymentProviders
{
    public const string Zarinpal = "zarinpal";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { Zarinpal };
}

/// <summary>
/// A payment for an order was verified with the provider (backend plan 9.10). The Orders module moves the order to <c>paid</c> when
/// it is awaiting payment and the amount equals its total; otherwise it notes the payment on the order's timeline for the seller.
/// </summary>
[IntegrationEvent(Topics.PaymentConfirmed)]
public sealed record PaymentConfirmed(
    Guid WorkspaceId, Guid OrderId, Guid PaymentLinkId, string Provider, Money Amount, string ReferenceId, string? CardMask, DateTimeOffset PaidAt) : IIntegrationEvent;
