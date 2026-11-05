using System.Numerics;
using Pishkhan.Persian;

namespace Pishkhan.Modules.Payments.Domain;

public static class ReceiptCheckKeys
{
    /// <summary>The model read the image as a bank receipt.</summary>
    public const string Extraction = "extraction";

    /// <summary>An order awaiting payment to match it with.</summary>
    public const string Order = "order";

    public const string Amount = "amount";

    /// <summary>The destination card or Sheba is one of the seller's accounts.</summary>
    public const string Destination = "destination";

    public const string TimeWindow = "time_window";

    /// <summary>The reference number looks like one (digits, 6 to 30).</summary>
    public const string Format = "format";

    public const string Duplicate = "duplicate";
}

public static class ReceiptCheckResults
{
    public const string Pass = "pass";
    public const string Fail = "fail";
    public const string Unknown = "unknown";
}

/// <summary>The order a receipt is checked against: its total (Rial) and when it was made.</summary>
public sealed record ReceiptOrder(Guid Id, long Total, DateTimeOffset CreatedAt);

/// <summary>A seller account a receipt's destination may name.</summary>
public sealed record ReceiptAccount(string? Card, string? Sheba);

/// <summary>Earlier receipts of the workspace: with the same reference number, the same image, or a similar image.</summary>
public sealed record ReceiptDuplicates(Guid? SameReference, Guid? SameImage, Guid? SimilarImage)
{
    public static readonly ReceiptDuplicates None = new(null, null, null);
}

/// <summary>
/// The checks of backend plan 10.5, step 3 and 4. Pure, so they are tested on their own. A receipt is auto-approved only when the
/// platform allows it, the seller turned it on, the extraction is confident enough and every check passes; an exact duplicate is the
/// only automatic rejection; everything else waits for the seller.
/// </summary>
public static class ReceiptRules
{
    /// <summary>Image hashes this many bits apart or fewer are the same picture (re-saved, resized or lightly cropped).</summary>
    public const int SimilarDistance = 6;

    /// <summary>A transfer up to this long before the order counts (the customer paid while the seller was writing it up).</summary>
    public static readonly TimeSpan Early = TimeSpan.FromHours(1);

    /// <summary>Clock differences between the bank's app and the server.</summary>
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(30);

    public static ReceiptVerdict Evaluate(
        ReceiptFields? fields, ReceiptOrder? order, IReadOnlyList<ReceiptAccount> accounts, ReceiptDuplicates duplicates, ReceiptSettings settings,
        bool autoApproveAvailable, DateTimeOffset submittedAt)
    {
        var checks = new List<ReceiptCheck>
        {
            fields is null ? new(ReceiptCheckKeys.Extraction, ReceiptCheckResults.Unknown, "unavailable")
                : !fields.IsReceipt ? new(ReceiptCheckKeys.Extraction, ReceiptCheckResults.Fail, "not_a_receipt")
                : new(ReceiptCheckKeys.Extraction, ReceiptCheckResults.Pass),
            order is null ? new(ReceiptCheckKeys.Order, ReceiptCheckResults.Unknown, "no_order") : new(ReceiptCheckKeys.Order, ReceiptCheckResults.Pass),
            AmountCheck(fields?.Amount, order, settings.AmountTolerance),
            DestinationCheck(fields, accounts),
            TimeCheck(fields?.PaidAt, order, settings.WindowHours, submittedAt),
            fields?.Reference is null ? new(ReceiptCheckKeys.Format, ReceiptCheckResults.Unknown) : new(ReceiptCheckKeys.Format, ReceiptCheckResults.Pass),
        };

        var exact = duplicates.SameReference ?? duplicates.SameImage;
        checks.Add(exact is not null ? new(ReceiptCheckKeys.Duplicate, ReceiptCheckResults.Fail, duplicates.SameReference is not null ? "same_reference" : "same_image")
            : duplicates.SimilarImage is not null ? new(ReceiptCheckKeys.Duplicate, ReceiptCheckResults.Fail, "similar_image")
            : new(ReceiptCheckKeys.Duplicate, ReceiptCheckResults.Pass));

        if (exact is not null)
        {
            return new ReceiptVerdict(ReceiptStatuses.Rejected, checks, exact);
        }

        var auto = autoApproveAvailable && settings.AutoApprove && fields is not null && fields.Confidence >= settings.Threshold
            && checks.All(c => c.Result == ReceiptCheckResults.Pass);
        return new ReceiptVerdict(auto ? ReceiptStatuses.AutoApproved : ReceiptStatuses.NeedsReview, checks, duplicates.SimilarImage);
    }

    private static ReceiptCheck AmountCheck(long? amount, ReceiptOrder? order, long tolerance) =>
        amount is not { } a || order is null ? new(ReceiptCheckKeys.Amount, ReceiptCheckResults.Unknown)
        : Math.Abs(a - order.Total) <= tolerance ? new(ReceiptCheckKeys.Amount, ReceiptCheckResults.Pass)
        : new(ReceiptCheckKeys.Amount, ReceiptCheckResults.Fail, a < order.Total ? "less" : "more");

    private static ReceiptCheck DestinationCheck(ReceiptFields? fields, IReadOnlyList<ReceiptAccount> accounts)
    {
        if (accounts.Count == 0)
        {
            return new(ReceiptCheckKeys.Destination, ReceiptCheckResults.Unknown, "no_accounts");
        }

        if (fields is null || (fields.DestinationCard is null && fields.DestinationSheba is null))
        {
            return new(ReceiptCheckKeys.Destination, ReceiptCheckResults.Unknown);
        }

        var match = accounts.Any(a =>
            (fields.DestinationCard is { } card && a.Card is { } registered && CardMatches(card, registered))
            || (fields.DestinationSheba is { } sheba && a.Sheba is { } iban && ShebaMatches(sheba, iban)));
        return new(ReceiptCheckKeys.Destination, match ? ReceiptCheckResults.Pass : ReceiptCheckResults.Fail);
    }

    private static ReceiptCheck TimeCheck(DateTimeOffset? paidAt, ReceiptOrder? order, int windowHours, DateTimeOffset submittedAt)
    {
        if (paidAt is not { } at || order is null)
        {
            return new(ReceiptCheckKeys.TimeWindow, ReceiptCheckResults.Unknown);
        }

        if (at < order.CreatedAt - Early)
        {
            return new(ReceiptCheckKeys.TimeWindow, ReceiptCheckResults.Fail, "before_order");
        }

        if (at > submittedAt + Skew)
        {
            return new(ReceiptCheckKeys.TimeWindow, ReceiptCheckResults.Fail, "in_future");
        }

        return at > order.CreatedAt + TimeSpan.FromHours(windowHours)
            ? new(ReceiptCheckKeys.TimeWindow, ReceiptCheckResults.Fail, "too_late")
            : new(ReceiptCheckKeys.TimeWindow, ReceiptCheckResults.Pass);
    }

    /// <summary>
    /// A printed card number (often masked: <c>6037-99**-****-1234</c>, <c>****1234</c>) matches a registered one when every printed
    /// digit agrees, aligned from the right, and at least four are printed.
    /// </summary>
    public static bool CardMatches(string printed, string registered)
    {
        var p = Masked(printed);
        var r = BankCard.Clean(registered);
        if (r is not { Length: 16 } || p.Length is 0 or > 16)
        {
            return false;
        }

        var offset = 16 - p.Length;
        var known = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '*')
            {
                continue;
            }

            if (p[i] != r[offset + i])
            {
                return false;
            }

            known++;
        }

        return known >= 4;
    }

    /// <summary>A Sheba as printed (sometimes masked in the middle) against a registered one, the same way as cards.</summary>
    public static bool ShebaMatches(string printed, string registered)
    {
        var r = Sheba.Clean(registered);
        if (r is not { Length: 26 })
        {
            return false;
        }

        var p = Masked(printed.ToUpperInvariant().Replace("IR", string.Empty, StringComparison.Ordinal));
        var digits = r[2..];
        if (p.Length is 0 or > 24)
        {
            return false;
        }

        var offset = 24 - p.Length;
        var known = 0;
        for (var i = 0; i < p.Length; i++)
        {
            if (p[i] == '*')
            {
                continue;
            }

            if (p[i] != digits[offset + i])
            {
                return false;
            }

            known++;
        }

        return known >= 8;
    }

    /// <summary>Digits only (Persian or Arabic digits read as Latin), 6 to 30 of them; otherwise null.</summary>
    public static string? CleanReference(string? printed)
    {
        if (string.IsNullOrWhiteSpace(printed))
        {
            return null;
        }

        var digits = new string([.. PersianDigits.ToLatin(printed).Where(char.IsAsciiDigit)]);
        return digits.Length is >= 6 and <= 30 ? digits : null;
    }

    /// <summary>Bits that differ between two image hashes.</summary>
    public static int Distance(long a, long b) => BitOperations.PopCount((ulong)(a ^ b));

    /// <summary>Digits kept, mask characters (<c>* x X • ●</c>) as <c>*</c>, everything else dropped.</summary>
    private static string Masked(string value) =>
        new([.. PersianDigits.ToLatin(value).Select(c => c is '*' or 'x' or 'X' or '•' or '●' or '×' ? '*' : c).Where(c => c == '*' || char.IsAsciiDigit(c))]);
}
