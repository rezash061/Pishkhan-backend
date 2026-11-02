using System.Security.Cryptography;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Payments.Domain;

/// <summary>The seller's own merchant account with a payment provider (backend plan 9.10). The merchant ID is stored encrypted.</summary>
public sealed class MerchantAccount : AggregateRoot<Guid>, ITenantOwned
{
    private MerchantAccount()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    public string MerchantIdEnc { get; private set; } = string.Empty;

    /// <summary>The last four characters, to show which merchant ID is set.</summary>
    public string MerchantIdHint { get; private set; } = string.Empty;

    /// <summary>The provider's test gateway: no money moves.</summary>
    public bool Sandbox { get; private set; }

    public bool Enabled { get; private set; }

    public static MerchantAccount Create(Guid workspaceId, string provider, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, Provider = provider, CreatedAt = now, UpdatedAt = now };

    public void Update(string provider, (string Enc, string Hint)? merchantId, bool sandbox, bool enabled, DateTimeOffset now)
    {
        Provider = provider;
        if (merchantId is { } m)
        {
            (MerchantIdEnc, MerchantIdHint) = m;
        }

        Sandbox = sandbox;
        Enabled = enabled;
        UpdatedAt = now;
    }
}

public static class PaymentLinkStatuses
{
    public const string Active = "active";
    public const string Paid = "paid";
    public const string Cancelled = "cancelled";

    /// <summary>Shown for an active link past its expiry; not stored.</summary>
    public const string Expired = "expired";
}

public static class PaymentAttemptStatuses
{
    public const string Started = "started";
    public const string Paid = "paid";
    public const string Failed = "failed";
}

/// <summary>An order line as the payment page shows it, fixed when the link is made.</summary>
public sealed record PaymentLinkLine(string Title, string? VariantLabel, int Quantity, long LineTotal);

/// <summary>
/// A payment link for one order: a random token in a public URL, and the amount fixed when the link is made. Only this amount is ever
/// sent to the provider or verified, whatever a callback says. A new link for the same order cancels the older ones.
/// </summary>
public sealed class PaymentLink : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxAttempts = 20;

    private PaymentLink()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid OrderId { get; private set; }

    public long OrderNumber { get; private set; }

    public string? ShopName { get; private set; }

    public List<PaymentLinkLine> Lines { get; private set; } = [];

    /// <summary>Rial.</summary>
    public long Amount { get; private set; }

    public string Provider { get; private set; } = string.Empty;

    /// <summary>32 URL-safe characters (192 random bits).</summary>
    public string Token { get; private set; } = string.Empty;

    public string Status { get; private set; } = PaymentLinkStatuses.Active;

    public DateTimeOffset ExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public string? ReferenceId { get; private set; }

    public string? CardMask { get; private set; }

    /// <summary>The code of the last failed attempt (the provider's, or <c>cancelled</c> when the customer left the gateway).</summary>
    public string? LastError { get; private set; }

    public static PaymentLink Create(
        Guid workspaceId, Guid orderId, long orderNumber, string? shopName, IReadOnlyList<PaymentLinkLine> lines, long amount, string provider, TimeSpan lifetime,
        Guid? createdBy, DateTimeOffset now) =>
        new()
        {
            Id = Uuid.NewV7(now), WorkspaceId = workspaceId, OrderId = orderId, OrderNumber = orderNumber, ShopName = shopName, Lines = [.. lines], Amount = amount,
            Provider = provider, Token = NewToken(), ExpiresAt = now + lifetime, CreatedBy = createdBy, CreatedAt = now, UpdatedAt = now,
        };

    public static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public string StatusAt(DateTimeOffset now) => Status == PaymentLinkStatuses.Active && now >= ExpiresAt ? PaymentLinkStatuses.Expired : Status;

    public bool CanStart(DateTimeOffset now) => StatusAt(now) == PaymentLinkStatuses.Active && Attempts < MaxAttempts;

    public void Started(DateTimeOffset now)
    {
        Attempts++;
        UpdatedAt = now;
    }

    public void Failed(string code, DateTimeOffset now)
    {
        LastError = code;
        UpdatedAt = now;
    }

    /// <summary>Records the verified payment. A link is paid once; cancelling or expiring it later does not undo a payment.</summary>
    public bool MarkPaid(string referenceId, string? cardMask, DateTimeOffset now)
    {
        if (Status == PaymentLinkStatuses.Paid)
        {
            return false;
        }

        // A payment the provider verified is money received: it counts even if the link was cancelled or expired meanwhile.
        Status = PaymentLinkStatuses.Paid;
        ReferenceId = referenceId;
        CardMask = cardMask;
        PaidAt = now;
        LastError = null;
        UpdatedAt = now;
        return true;
    }

    public bool Cancel(DateTimeOffset now)
    {
        if (Status != PaymentLinkStatuses.Active)
        {
            return false;
        }

        Status = PaymentLinkStatuses.Cancelled;
        UpdatedAt = now;
        return true;
    }
}

/// <summary>One trip to the gateway: the provider's authority (its id for this payment) and how it ended.</summary>
public sealed class PaymentAttempt : Entity<Guid>, ITenantOwned
{
    private PaymentAttempt()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid LinkId { get; private set; }

    public string Authority { get; private set; } = string.Empty;

    public string Status { get; private set; } = PaymentAttemptStatuses.Started;

    public string? Code { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public static PaymentAttempt Start(PaymentLink link, string authority, DateTimeOffset now) =>
        new() { Id = Uuid.NewV7(now), WorkspaceId = link.WorkspaceId, LinkId = link.Id, Authority = authority, CreatedAt = now, UpdatedAt = now };

    public void Complete(bool paid, string? code, DateTimeOffset now)
    {
        Status = paid ? PaymentAttemptStatuses.Paid : PaymentAttemptStatuses.Failed;
        Code = code;
        CompletedAt = now;
        UpdatedAt = now;
    }
}
