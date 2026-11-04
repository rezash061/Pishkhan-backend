using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.AI;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Conversations.Contracts;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Payments.Contracts;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using SkiaSharp;

namespace Pishkhan.Modules.Payments.Services;

/// <summary>A 64-bit difference hash: the image shrunk to 9×8 grey pixels, one bit per pair of neighbours. Null when it is not an image.</summary>
internal static class ImageHash
{
    public static long? Compute(byte[] bytes)
    {
        using var original = SKBitmap.Decode(bytes);
        if (original is null)
        {
            return null;
        }

        using var small = original.Resize(new SKImageInfo(9, 8, SKColorType.Gray8), new SKSamplingOptions(SKFilterMode.Linear));
        if (small is null)
        {
            return null;
        }

        ulong hash = 0;
        var bit = 0;
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                if (Grey(small.GetPixel(x, y)) > Grey(small.GetPixel(x + 1, y)))
                {
                    hash |= 1UL << bit;
                }

                bit++;
            }
        }

        return unchecked((long)hash);
    }

    private static int Grey(SKColor c) => (c.Red * 299 + c.Green * 587 + c.Blue * 114) / 1000;
}

/// <summary>
/// Reads a receipt image with a vision model (task <c>extract_receipt</c>, prompt <c>receipt.extract</c>) into <see cref="ReceiptFields"/>.
/// Persian digits are read as Latin and Toman amounts become Rial. A model outage or an answer that is not valid json gives null, and
/// the receipt goes to the seller with the extraction marked unavailable.
/// </summary>
internal sealed class ReceiptExtractor(IModelRouter router, IPromptRegistry prompts, ILogger<ReceiptExtractor> logger)
{
    public const string PromptKey = "receipt.extract";

    public async Task<ReceiptFields?> ExtractAsync(byte[] image, string contentType, Guid workspaceId, CancellationToken ct)
    {
        try
        {
            var prompt = prompts.Get(PromptKey);
            var response = await router.GetChatClient(AiTasks.ExtractReceipt, workspaceId).GetResponseAsync(
                [new ChatMessage(ChatRole.System, prompt.Text), new ChatMessage(ChatRole.User, [new DataContent(image, contentType)])],
                new ChatOptions { ResponseFormat = ChatResponseFormat.Json, Temperature = 0 },
                ct);
            return Parse(response.Text) ?? throw new InvalidDataException("The model's receipt was not valid json");
        }
        catch (Exception e) when (e is AllRoutesFailedException or NoRouteException or KeyNotFoundException or InvalidDataException)
        {
            logger.LogWarning("Receipt extraction unavailable: {Error}", e.Message);
            return null;
        }
    }

    /// <summary>
    /// Reads <c>{"is_receipt", "amount", "currency" ("rial"|"toman"), "paid_at" (ISO 8601), "reference", "source_card", "destination_card",
    /// "destination_sheba", "bank", "confidence"}</c>; null when it is not a json object.
    /// </summary>
    public static ReceiptFields? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var amount = Number(root, "amount");
            if (amount is { } a && string.Equals(Text(root, "currency", 10), "toman", StringComparison.OrdinalIgnoreCase))
            {
                amount = a * 10;
            }

            DateTimeOffset? paidAt = Text(root, "paid_at", 40) is { } at
                && DateTimeOffset.TryParse(PersianDigits.ToLatin(at), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed.ToUniversalTime() : null;
            var confidence = root.TryGetProperty("confidence", out var c) && c.ValueKind == JsonValueKind.Number ? Math.Clamp(c.GetDouble(), 0, 1) : 0;
            var isReceipt = !root.TryGetProperty("is_receipt", out var r) || r.ValueKind != JsonValueKind.False;
            return new ReceiptFields(
                amount is > 0 and < 1_000_000_000_000 ? amount : null, paidAt, ReceiptRules.CleanReference(Text(root, "reference", 60)), Text(root, "source_card", 40),
                Text(root, "destination_card", 40), Text(root, "destination_sheba", 40), Text(root, "bank", 60), confidence, isReceipt);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement root, string name, int max) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s && !string.IsNullOrWhiteSpace(s)
            ? (s.Length > max ? s[..max] : s).Trim()
            : null;

    private static long? Number(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var v))
        {
            return null;
        }

        if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out var d))
        {
            return (long)Math.Round(d);
        }

        if (v.ValueKind == JsonValueKind.String)
        {
            var digits = new string([.. PersianDigits.ToLatin(v.GetString() ?? string.Empty).Where(char.IsAsciiDigit)]);
            return digits.Length is > 0 and <= 15 ? long.Parse(digits, CultureInfo.InvariantCulture) : null;
        }

        return null;
    }
}

internal static class ReceiptNotices
{
    public const string Kind = "receipt";

    public static string Link(Guid receiptId) => $"/orders/receipts?receipt={receiptId}";

    public static void Publish(PaymentsDbContext db, Receipt receipt, string title, string body) =>
        db.Publish(new NotificationRequested(receipt.WorkspaceId, null, Kind, title, body, Link(receipt.Id)), receipt.WorkspaceId.ToString(), workspaceId: receipt.WorkspaceId);

    /// <summary>The verified card-to-card payment the Orders module moves the order to <c>paid</c> with.</summary>
    public static void Confirm(PaymentsDbContext db, Receipt receipt, Guid orderId, Money amount, DateTimeOffset now) =>
        db.Publish(
            new PaymentConfirmed(
                receipt.WorkspaceId, orderId, receipt.Id, PaymentProviders.CardToCard, amount, receipt.Reference ?? receipt.Id.ToString("N")[..12],
                receipt.SourceCard, receipt.PaidAt ?? now),
            orderId.ToString(), workspaceId: receipt.WorkspaceId);
}

/// <summary>Starting the checks for a receipt whose file is clean; shared by the scan handler and the submit paths.</summary>
internal static class ReceiptStart
{
    public static void Begin(PaymentsDbContext db, Receipt receipt, DateTimeOffset now)
    {
        receipt.Scanned(now);
        db.Publish(new ReceiptSubmitted(receipt.WorkspaceId, receipt.Id), receipt.Id.ToString(), workspaceId: receipt.WorkspaceId);
    }

    /// <summary>
    /// After the receipt is saved: when its file was already scanned clean (the scan finished before the receipt existed), start now.
    /// Otherwise the scan's event starts it.
    /// </summary>
    public static async Task CatchUpAsync(PaymentsDbContext db, IFileService files, Guid receiptId, IClock clock, CancellationToken ct)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is not { Status: ReceiptStatuses.Scanning, FileId: { } fileId } || await files.GetCleanAsync(fileId, ct) is null)
        {
            return;
        }

        Begin(db, receipt, clock.UtcNow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The scan handler started it meanwhile.
        }
    }
}

/// <summary>Commerce group: a receipt's file finished scanning. Clean starts the checks; anything else rejects it as unsafe.</summary>
internal sealed class ReceiptScanHandler(PaymentsDbContext db, IClock clock) : IdempotentHandler<FileScanCompleted, PaymentsDbContext>(db)
{
    protected override async Task HandleOnceAsync(FileScanCompleted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (message.Purpose != FilePurposes.Receipt)
        {
            return;
        }

        var receipt = await Db.Receipts.FirstOrDefaultAsync(r => r.FileId == message.FileId && r.Status == ReceiptStatuses.Scanning, cancellationToken);
        if (receipt is null)
        {
            return;
        }

        var now = clock.UtcNow;
        if (message.Status == FileStatuses.Clean)
        {
            ReceiptStart.Begin(Db, receipt, now);
        }
        else if (message.Status != FileStatuses.PendingScan)
        {
            receipt.Unsafe(now);
        }
    }
}

/// <summary>
/// Commerce group (backend plan 10.5 steps 2 to 4): reads the receipt, matches the order, checks it against the seller's accounts and
/// earlier receipts, and decides. Needs review notifies the team; auto-approved confirms the payment.
/// </summary>
internal sealed class ReceiptSubmittedHandler(
    PaymentsDbContext db, IFileService files, IObjectStorage storage, ReceiptExtractor extractor, IOrderQueries orders, IEntitlements entitlements,
    IOptions<PaymentsOptions> options, IClock clock, ILogger<ReceiptSubmittedHandler> logger)
    : IdempotentHandler<ReceiptSubmitted, PaymentsDbContext>(db)
{
    /// <summary>How far back near-duplicate images are compared.</summary>
    public static readonly TimeSpan DuplicateHorizon = TimeSpan.FromDays(180);

    protected override async Task HandleOnceAsync(ReceiptSubmitted message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var receipt = await Db.Receipts.FirstOrDefaultAsync(r => r.Id == message.ReceiptId, cancellationToken);
        if (receipt is not { Status: ReceiptStatuses.Processing, FileId: { } fileId })
        {
            return;
        }

        var now = clock.UtcNow;
        var file = await files.GetCleanAsync(fileId, cancellationToken);
        if (file is null)
        {
            receipt.Unsafe(now);
            return;
        }

        byte[] bytes;
        await using (var stream = await storage.OpenReadAsync(file.Bucket, file.StorageKey, cancellationToken))
        {
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, cancellationToken);
            bytes = buffer.ToArray();
        }

        var isImage = file.ContentType.StartsWith("image/", StringComparison.Ordinal);
        long? hash = null;
        if (isImage)
        {
            try
            {
                hash = ImageHash.Compute(bytes);
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException)
            {
                logger.LogWarning("Receipt {ReceiptId}: image hash failed: {Error}", receipt.Id, e.Message);
            }
        }

        // Metered: one verification per receipt read by the model; past the plan's limit the seller reads it.
        var plan = await entitlements.GetAsync(receipt.WorkspaceId, cancellationToken);
        var allowed = plan.Limit(BillingMeters.ReceiptVerifications) is null
            || plan.Allows(BillingMeters.ReceiptVerifications, await entitlements.GetUsageAsync(receipt.WorkspaceId, BillingMeters.ReceiptVerifications, cancellationToken) + 1);
        ReceiptFields? fields = null;
        if (isImage && allowed)
        {
            fields = await extractor.ExtractAsync(bytes, file.ContentType, receipt.WorkspaceId, cancellationToken);
            if (fields is not null)
            {
                Db.Publish(new UsageRecorded(receipt.WorkspaceId, BillingMeters.ReceiptVerifications, 1, now, receipt.Id), receipt.WorkspaceId.ToString(), workspaceId: receipt.WorkspaceId);
            }
        }

        receipt.Read(fields, hash, now);

        var order = receipt.OrderId is { } orderId ? await orders.GetForPaymentAsync(orderId, cancellationToken)
            : receipt.ContactId is { } contactId ? await orders.FindAwaitingPaymentAsync(contactId, cancellationToken)
            : null;
        if (order is not null)
        {
            receipt.MatchOrder(order.Id, order.ContactId, order.ConversationId);
        }

        var accounts = await Db.BankAccounts.AsNoTracking().Where(a => a.Enabled).Select(a => new ReceiptAccount(a.CardNumber, a.Sheba)).ToListAsync(cancellationToken);
        var settings = await Db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(cancellationToken) ?? ReceiptSettings.Default(receipt.WorkspaceId);
        var duplicates = await DuplicatesAsync(receipt, now, cancellationToken);
        var verdict = ReceiptRules.Evaluate(
            fields, order is null ? null : new ReceiptOrder(order.Id, order.Total.Amount, order.CreatedAt), accounts, duplicates, settings,
            options.Value.ReceiptAutoApprove, receipt.CreatedAt);
        if (fields is null)
        {
            var reason = !isImage ? "not_an_image" : !allowed ? "plan_limit" : "unavailable";
            verdict = verdict with { Checks = [.. verdict.Checks.Select(c => c.Key == ReceiptCheckKeys.Extraction ? c with { Detail = reason } : c)] };
        }

        receipt.Decide(verdict, now);
        var number = order is null ? string.Empty : $" سفارش {PersianDigits.ToPersian(order.Number.ToString(CultureInfo.InvariantCulture))}";
        switch (verdict.Status)
        {
            case ReceiptStatuses.AutoApproved:
                ReceiptNotices.Confirm(Db, receipt, order!.Id, order.Total, now);
                ReceiptNotices.Publish(Db, receipt, "رسید کارت به کارت تأیید شد", $"رسید{number} همه‌ی بررسی‌ها را گذراند و خودکار تأیید شد.");
                break;
            case ReceiptStatuses.NeedsReview:
                ReceiptNotices.Publish(Db, receipt, "رسید تازه منتظر بررسی است", $"رسید کارت به کارت{number} را بررسی و تأیید یا رد کنید.");
                break;
            default:
                ReceiptNotices.Publish(Db, receipt, "رسید تکراری رد شد", $"این رسید{number} پیش‌تر ارسال شده بود؛ اگر اشتباه است می‌توانید تأییدش کنید.");
                break;
        }

        logger.LogInformation("Receipt {ReceiptId}: {Status}", receipt.Id, verdict.Status);
    }

    private async Task<ReceiptDuplicates> DuplicatesAsync(Receipt receipt, DateTimeOffset now, CancellationToken ct)
    {
        var others = Db.Receipts.AsNoTracking().Where(r => r.Id != receipt.Id && r.RejectReason != ReceiptRejections.Unsafe);
        var sameReference = receipt.Reference is { } reference
            ? await others.Where(r => r.Reference == reference).OrderBy(r => r.CreatedAt).Select(r => (Guid?)r.Id).FirstOrDefaultAsync(ct)
            : null;
        Guid? sameImage = null;
        Guid? similar = null;
        if (receipt.ImageHash is { } hash)
        {
            var since = now - DuplicateHorizon;
            var hashes = await others.Where(r => r.ImageHash != null && r.CreatedAt > since).OrderByDescending(r => r.CreatedAt).Take(5_000)
                .Select(r => new { r.Id, Hash = r.ImageHash!.Value }).ToListAsync(ct);
            sameImage = hashes.FirstOrDefault(h => h.Hash == hash)?.Id;
            similar = hashes.Where(h => h.Hash != hash).Select(h => new { h.Id, Distance = ReceiptRules.Distance(h.Hash, hash) })
                .Where(h => h.Distance <= ReceiptRules.SimilarDistance).OrderBy(h => h.Distance).FirstOrDefault()?.Id;
        }

        return new ReceiptDuplicates(sameReference, sameImage, similar);
    }
}

/// <summary>
/// Commerce group (backend plan 10.5 step 1): an image a customer sends while they have an order awaiting payment, in a workspace that
/// takes card-to-card payments (has a bank account), is taken as a receipt: copied into the Files module, scanned and checked.
/// </summary>
internal sealed class ReceiptMessageHandler(
    PaymentsDbContext db, IOrderQueries orders, IConversationQueries conversations, SafeHttpClient http, IFileService files, IClock clock,
    ILogger<ReceiptMessageHandler> logger)
    : IdempotentHandler<InboundMessageReceived, PaymentsDbContext>(db)
{
    private Guid? _receiptId;

    protected override async Task HandleOnceAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.WorkspaceId is not { } workspaceId || message.IsRepeat || message.Kind != InboundKinds.Attachment || message.AttachmentCount == 0
            || !await Db.BankAccounts.AnyAsync(a => a.Enabled, cancellationToken)
            || await Db.Receipts.AnyAsync(r => r.MessageId == message.MessageId, cancellationToken))
        {
            return;
        }

        var order = await orders.FindAwaitingPaymentAsync(message.ContactId, cancellationToken);
        var image = order is null ? null : await conversations.GetInboundImageAsync(message.MessageId, cancellationToken);
        if (order is null || image is null || !Uri.TryCreate(image.Url, UriKind.Absolute, out var url))
        {
            return;
        }

        Guid fileId;
        try
        {
            var download = await http.DownloadAsync(url, ReceiptSubmission.MaxBytes, SafeHttpClient.MetaCdnSuffixes, cancellationToken: cancellationToken);
            using (download.Content)
            {
                fileId = await files.IngestAsync(workspaceId, FilePurposes.Receipt, download.Content, download.ContentType, "receipt", cancellationToken);
            }
        }
        catch (Exception ex) when (ex is SafeHttpException or HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Receipt image of message {MessageId} not downloaded: {Error}", message.MessageId, ex.Message);
            return;
        }

        var receipt = Receipt.Submit(workspaceId, ReceiptSources.Message, fileId, order.Id, message.ContactId, message.ConversationId, message.MessageId, null, clock.UtcNow);
        Db.Receipts.Add(receipt);
        _receiptId = receipt.Id;
    }

    protected override async Task AfterCommitAsync(InboundMessageReceived message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (_receiptId is { } id)
        {
            await ReceiptStart.CatchUpAsync(Db, files, id, clock, cancellationToken);
        }
    }
}

/// <summary>Receipts a member submits in the panel: an uploaded file (scanned clean) or an image from a customer's message.</summary>
internal sealed class ReceiptSubmission(
    PaymentsDbContext db, IFileService files, IConversationQueries conversations, IOrderQueries orders, SafeHttpClient http, IClock clock)
{
    public const long MaxBytes = 10 * 1024 * 1024;

    public async Task<(Receipt? Receipt, Error? Error)> SubmitAsync(Guid workspaceId, Guid? fileId, Guid? messageId, Guid? orderId, Guid userId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        OrderForPayment? order = null;
        if (orderId is { } id && (order = await orders.GetForPaymentAsync(id, ct)) is null)
        {
            return (null, Error.NotFound("payments.receipt.order_not_found"));
        }

        Receipt receipt;
        if (fileId is { } f)
        {
            var file = await files.GetCleanAsync(f, ct);
            if (file is null || file.Purpose != FilePurposes.Receipt)
            {
                return (null, Error.Unprocessable("payments.receipt.file_not_clean"));
            }

            if (await db.Receipts.AnyAsync(r => r.FileId == f, ct))
            {
                return (null, Error.Conflict("payments.receipt.already_submitted"));
            }

            receipt = Receipt.Submit(workspaceId, ReceiptSources.Panel, f, order?.Id, order?.ContactId, order?.ConversationId, null, userId, now);
            db.Receipts.Add(receipt);
            ReceiptStart.Begin(db, receipt, now);
            await db.SaveChangesAsync(ct);
            return (receipt, null);
        }

        var image = await conversations.GetInboundImageAsync(messageId!.Value, ct);
        if (image is null || !Uri.TryCreate(image.Url, UriKind.Absolute, out var url))
        {
            return (null, Error.Unprocessable("payments.receipt.no_image"));
        }

        if (await db.Receipts.AnyAsync(r => r.MessageId == image.MessageId, ct))
        {
            return (null, Error.Conflict("payments.receipt.already_submitted"));
        }

        order ??= await orders.FindAwaitingPaymentAsync(image.ContactId, ct);
        Guid stored;
        try
        {
            var download = await http.DownloadAsync(url, MaxBytes, SafeHttpClient.MetaCdnSuffixes, cancellationToken: ct);
            using (download.Content)
            {
                stored = await files.IngestAsync(workspaceId, FilePurposes.Receipt, download.Content, download.ContentType, "receipt", ct);
            }
        }
        catch (Exception ex) when (ex is SafeHttpException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // Instagram's links expire: an old image can no longer be fetched.
            return (null, Error.Unprocessable("payments.receipt.image_unavailable"));
        }

        receipt = Receipt.Submit(workspaceId, ReceiptSources.Message, stored, order?.Id, image.ContactId, image.ConversationId, image.MessageId, userId, now);
        db.Receipts.Add(receipt);
        await db.SaveChangesAsync(ct);
        await ReceiptStart.CatchUpAsync(db, files, receipt.Id, clock, ct);
        return (receipt, null);
    }
}
