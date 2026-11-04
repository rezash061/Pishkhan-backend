using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Payments.Domain;

/// <summary>
/// A bank account the seller receives card-to-card payments into (backend plan 9.10), at least a valid card number (Luhn) or Sheba
/// (mod 97). A receipt's destination must match one of the enabled ones.
/// </summary>
public sealed class BankAccount : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxPerWorkspace = 10;

    private BankAccount()
    {
    }

    public Guid WorkspaceId { get; private set; }

    /// <summary>16 digits, or null.</summary>
    public string? CardNumber { get; private set; }

    /// <summary>IR and 24 digits, or null.</summary>
    public string? Sheba { get; private set; }

    public string? HolderName { get; private set; }

    /// <summary>From the card's BIN or the Sheba's bank code; null when unknown.</summary>
    public string? BankCode { get; private set; }

    public bool Enabled { get; private set; } = true;

    public static BankAccount Create(Guid workspaceId, DateTimeOffset now) => new() { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, CreatedAt = now, UpdatedAt = now };

    public void Update(string? card, string? sheba, string? holder, string? bank, bool enabled, DateTimeOffset now)
    {
        CardNumber = card;
        Sheba = sheba;
        HolderName = holder;
        BankCode = bank;
        Enabled = enabled;
        UpdatedAt = now;
    }
}

/// <summary>
/// How a workspace's receipts are decided. Auto-approve is off by default and can only be turned on where the platform allows it
/// (after extraction reached 95% precision on the labeled receipt set, BE-2.10).
/// </summary>
public sealed class ReceiptSettings : ITenantOwned
{
    public const double MinThreshold = 0.8;
    public const long MaxTolerance = 1_000_000;
    public const int MaxWindowHours = 720;

    public Guid WorkspaceId { get; init; }

    public bool AutoApprove { get; set; }

    /// <summary>The extraction confidence (0 to 1) an auto-approved receipt needs.</summary>
    public double Threshold { get; set; } = 0.9;

    /// <summary>How far the amount may be from the order's total (Rial).</summary>
    public long AmountTolerance { get; set; }

    /// <summary>The transfer must be at most this many hours after the order was made.</summary>
    public int WindowHours { get; set; } = 72;

    public DateTimeOffset UpdatedAt { get; set; }

    public static ReceiptSettings Default(Guid workspaceId) => new() { WorkspaceId = workspaceId };
}

public static class ReceiptStatuses
{
    /// <summary>The image is being scanned for malware.</summary>
    public const string Scanning = "scanning";

    /// <summary>Clean; extraction and checks are running.</summary>
    public const string Processing = "processing";

    public const string NeedsReview = "needs_review";
    public const string AutoApproved = "auto_approved";
    public const string Approved = "approved";
    public const string Rejected = "rejected";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { Scanning, Processing, NeedsReview, AutoApproved, Approved, Rejected };
}

public static class ReceiptSources
{
    /// <summary>An image the customer sent in a conversation with an order awaiting payment.</summary>
    public const string Message = "message";

    /// <summary>Uploaded or picked by a member in the panel.</summary>
    public const string Panel = "panel";
}

public static class ReceiptRejections
{
    /// <summary>The same receipt (reference number or image) was already submitted; the only automatic rejection.</summary>
    public const string Duplicate = "duplicate";

    /// <summary>The file was not clean.</summary>
    public const string Unsafe = "unsafe_file";
}

/// <summary>What the model read off a receipt. Amounts in Rial; card numbers are as printed (often masked).</summary>
public sealed record ReceiptFields(
    long? Amount, DateTimeOffset? PaidAt, string? Reference, string? SourceCard, string? DestinationCard, string? DestinationSheba, string? Bank,
    double Confidence, bool IsReceipt);

/// <summary>One check: <c>pass</c>, <c>fail</c> or <c>unknown</c> (the field could not be read, or there is nothing to compare with).</summary>
public sealed record ReceiptCheck(string Key, string Result, string? Detail = null);

/// <summary>
/// A card-to-card receipt (backend plan 10.5): the image, what was read off it, the checks against the order and the seller's accounts,
/// and the decision. Only the seller approves or rejects, except exact duplicates (rejected at once) and auto-approve when enabled.
/// </summary>
public sealed class Receipt : AggregateRoot<Guid>, ITenantOwned
{
    public const int MaxReason = 300;

    private Receipt()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Source { get; private set; } = ReceiptSources.Panel;

    public Guid? FileId { get; private set; }

    public Guid? OrderId { get; private set; }

    public Guid? ContactId { get; private set; }

    public Guid? ConversationId { get; private set; }

    public Guid? MessageId { get; private set; }

    public string Status { get; private set; } = ReceiptStatuses.Scanning;

    public long? Amount { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    /// <summary>Digits only, so the same number printed differently still matches.</summary>
    public string? Reference { get; private set; }

    public string? SourceCard { get; private set; }

    public string? DestinationCard { get; private set; }

    public string? DestinationSheba { get; private set; }

    public string? Bank { get; private set; }

    public double? Confidence { get; private set; }

    /// <summary>64-bit difference hash of the image, for near-duplicate detection.</summary>
    public long? ImageHash { get; private set; }

    public List<ReceiptCheck> Checks { get; private set; } = [];

    public Guid? DuplicateOfId { get; private set; }

    public string? RejectReason { get; private set; }

    public Guid? SubmittedBy { get; private set; }

    /// <summary>Null when decided automatically.</summary>
    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static Receipt Submit(
        Guid workspaceId, string source, Guid fileId, Guid? orderId, Guid? contactId, Guid? conversationId, Guid? messageId, Guid? userId, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Source = source,
        FileId = fileId,
        OrderId = orderId,
        ContactId = contactId,
        ConversationId = conversationId,
        MessageId = messageId,
        SubmittedBy = userId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public bool IsOpen => Status is ReceiptStatuses.Scanning or ReceiptStatuses.Processing or ReceiptStatuses.NeedsReview;

    public void Scanned(DateTimeOffset now)
    {
        Status = ReceiptStatuses.Processing;
        UpdatedAt = now;
    }

    public void Read(ReceiptFields? fields, long? imageHash, DateTimeOffset now)
    {
        if (fields is not null)
        {
            Amount = fields.Amount;
            PaidAt = fields.PaidAt;
            Reference = fields.Reference;
            SourceCard = fields.SourceCard;
            DestinationCard = fields.DestinationCard;
            DestinationSheba = fields.DestinationSheba;
            Bank = fields.Bank;
            Confidence = fields.Confidence;
        }

        ImageHash = imageHash;
        UpdatedAt = now;
    }

    public void MatchOrder(Guid orderId, Guid? contactId, Guid? conversationId)
    {
        OrderId = orderId;
        ContactId ??= contactId;
        ConversationId ??= conversationId;
    }

    public void Decide(ReceiptVerdict verdict, DateTimeOffset now)
    {
        Checks = [.. verdict.Checks];
        DuplicateOfId = verdict.DuplicateOfId;
        Status = verdict.Status;
        RejectReason = verdict.Status == ReceiptStatuses.Rejected ? ReceiptRejections.Duplicate : null;
        DecidedAt = verdict.Status == ReceiptStatuses.NeedsReview ? null : now;
        UpdatedAt = now;
    }

    public void Unsafe(DateTimeOffset now)
    {
        Status = ReceiptStatuses.Rejected;
        RejectReason = ReceiptRejections.Unsafe;
        DecidedAt = now;
        UpdatedAt = now;
    }

    /// <summary>A member approves one in review, or one rejected automatically as a duplicate (the seller has the last word).</summary>
    public bool CanApprove => Status == ReceiptStatuses.NeedsReview || (Status == ReceiptStatuses.Rejected && DecidedBy is null && RejectReason == ReceiptRejections.Duplicate);

    public void Approve(Guid orderId, Guid userId, DateTimeOffset now)
    {
        OrderId = orderId;
        Status = ReceiptStatuses.Approved;
        RejectReason = null;
        DecidedBy = userId;
        DecidedAt = now;
        UpdatedAt = now;
    }

    public void Reject(string reason, Guid userId, DateTimeOffset now)
    {
        Status = ReceiptStatuses.Rejected;
        RejectReason = reason;
        DecidedBy = userId;
        DecidedAt = now;
        UpdatedAt = now;
    }
}

/// <summary>The checks' outcome: <c>needs_review</c>, <c>auto_approved</c> or <c>rejected</c> (exact duplicate).</summary>
public sealed record ReceiptVerdict(string Status, IReadOnlyList<ReceiptCheck> Checks, Guid? DuplicateOfId);
