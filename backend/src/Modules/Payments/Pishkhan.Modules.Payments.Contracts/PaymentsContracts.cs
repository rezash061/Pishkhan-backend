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

/// <summary>A payment opened at Zarinpal: its authority and where to send the payer, or Zarinpal's error code (<c>unavailable</c> when unreachable).</summary>
public sealed record ZarinpalStart(string? Authority, Uri? RedirectUrl, string? Error)
{
    public bool Ok => Error is null;
}

/// <summary>A verification: <see cref="Ok"/> only when Zarinpal confirmed the payment of exactly the amount asked.</summary>
public sealed record ZarinpalVerify(bool Ok, string Code, string? ReferenceId, string? CardMask);

/// <summary>
/// Zarinpal for other modules that take payments into an account of their own (Billing: subscriptions paid to Pishkhan). The caller
/// passes the merchant ID and keeps the amount; verify with that same amount, never one from the callback.
/// </summary>
public interface IZarinpalClient
{
    Task<ZarinpalStart> StartAsync(string merchantId, bool sandbox, long amountRial, Uri callback, string description, CancellationToken cancellationToken);

    Task<ZarinpalVerify> VerifyAsync(string merchantId, bool sandbox, long amountRial, string authority, CancellationToken cancellationToken);
}
