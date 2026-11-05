using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Orders.Contracts;
using Pishkhan.Modules.Payments.Domain;
using Pishkhan.Modules.Payments.Persistence;
using Pishkhan.Modules.Payments.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Persian;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Payments.Features;

/// <summary>A card number (16 digits, Luhn) and/or a Sheba (IR + 24 digits, mod 97); Persian digits, spaces and dashes are accepted.</summary>
public sealed record BankAccountRequest(string? CardNumber, string? Sheba, string? HolderName, bool Enabled = true);

/// <summary><c>bank</c> is the bank's code (from the card's BIN or the Sheba), <c>bankName</c> its Persian name; null when unknown.</summary>
public sealed record BankAccountResponse(
    Guid Id, string? CardNumber, string? CardMask, string? Sheba, string? HolderName, string? Bank, string? BankName, bool Enabled, DateTimeOffset CreatedAt);

/// <summary>
/// <c>autoApprove</c> can be on only when <c>autoApproveAvailable</c> (the platform's extraction met its precision bar). <c>threshold</c>
/// is the confidence (0.8 to 1) an auto-approved receipt needs; <c>amountTolerance</c> (Rial, up to 1,000,000) how far the amount may be
/// from the order's total; <c>windowHours</c> (1 to 720) how long after the order the transfer may be.
/// </summary>
public sealed record ReceiptSettingsRequest(bool AutoApprove, double Threshold = 0.9, long AmountTolerance = 0, int WindowHours = 72);

public sealed record ReceiptSettingsResponse(bool AutoApprove, bool AutoApproveAvailable, double Threshold, Money AmountTolerance, int WindowHours);

/// <summary>Either <c>fileId</c> (an uploaded file of purpose <c>receipt</c>, scanned clean) or <c>messageId</c> (a customer's image). <c>orderId</c> is optional: without it the contact's order awaiting payment is used.</summary>
public sealed record SubmitReceiptRequest(Guid? FileId, Guid? MessageId, Guid? OrderId);

/// <summary><c>orderId</c>: the order the payment is for, when the receipt has none or a different one.</summary>
public sealed record ApproveReceiptRequest(Guid? OrderId);

public sealed record RejectReceiptRequest(string Reason);

/// <summary>
/// A receipt and its checks. <c>status</c>: scanning, processing, needs_review, auto_approved, approved or rejected (<c>rejectReason</c>
/// <c>duplicate</c> and <c>unsafe_file</c> are automatic). Each check is <c>pass</c>, <c>fail</c> or <c>unknown</c>; keys: extraction,
/// order, amount, destination, time_window, format, duplicate. <c>imageUrl</c> (10 minutes) is set on the single receipt only.
/// </summary>
public sealed record ReceiptResponse(
    Guid Id, string Source, string Status, Guid? OrderId, Guid? ContactId, Guid? ConversationId, Guid? MessageId, Guid? FileId, Money? Amount, DateTimeOffset? PaidAt,
    string? Reference, string? SourceCard, string? DestinationCard, string? DestinationSheba, string? Bank, double? Confidence, IReadOnlyList<ReceiptCheck> Checks,
    Guid? DuplicateOfId, string? RejectReason, Guid? SubmittedBy, Guid? DecidedBy, DateTimeOffset? DecidedAt, DateTimeOffset CreatedAt, string? ImageUrl);

public sealed record ReceiptPage(IReadOnlyList<ReceiptResponse> Items, string? NextCursor);

/// <summary>Card-to-card receipts (BE-2.10, gap G1; backend plan 9.10 and 10.5): seller bank accounts, the review queue and decisions.</summary>
internal static class ReceiptEndpoints
{
    public const int PageSize = 50;

    public static void Map(IEndpointRouteBuilder app)
    {
        var ws = app.MapPanelApi().MapGroup("/workspaces/{workspaceId:guid}").WithTags("Receipts");

        ws.MapGet("/payments/bank-accounts", ListAccountsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("Bank accounts that receive card-to-card payments")
            .Produces<IReadOnlyList<BankAccountResponse>>();

        ws.MapPost("/payments/bank-accounts", CreateAccountAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Register a bank card and/or Sheba for receipt matching")
            .WithDescription("400 `payments.bank_account.card_invalid`, `payments.bank_account.sheba_invalid` or `payments.bank_account.empty`; 409 `payments.bank_account.duplicate` or `payments.bank_account.limit` (10).")
            .Produces<BankAccountResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        ws.MapPut("/payments/bank-accounts/{accountId:guid}", UpdateAccountAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Change a bank account")
            .Produces<BankAccountResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();

        ws.MapDelete("/payments/bank-accounts/{accountId:guid}", DeleteAccountAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Remove a bank account; receipts already checked keep their results")
            .Produces(StatusCodes.Status204NoContent);

        ws.MapGet("/payments/receipt-settings", GetSettingsAsync)
            .RequireWorkspacePermission(Permissions.SettingsRead)
            .WithSummary("How receipts are decided")
            .Produces<ReceiptSettingsResponse>();

        ws.MapPut("/payments/receipt-settings", PutSettingsAsync)
            .RequireWorkspacePermission(Permissions.SettingsWrite)
            .WithSummary("Change how receipts are decided")
            .WithDescription("422 `payments.receipts.auto_approve_unavailable` when turning auto-approve on before the platform allows it; 400 for values out of range.")
            .Produces<ReceiptSettingsResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapGet("/receipts", ListAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("Receipts, newest first (50 per page)")
            .WithDescription("Filters: `status`, `orderId`. `cursor` is the previous page's `nextCursor`.")
            .Produces<ReceiptPage>()
            .ProducesValidationProblem();

        ws.MapGet("/receipts/{receiptId:guid}", GetAsync)
            .RequireWorkspacePermission(Permissions.OrdersRead)
            .WithSummary("One receipt with its image")
            .Produces<ReceiptResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        ws.MapPost("/receipts", SubmitAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithIdempotency()
            .WithSummary("Submit a receipt: an uploaded file or a customer's image")
            .WithDescription("Exactly one of `fileId` and `messageId`. 404 `payments.receipt.order_not_found`; 422 `payments.receipt.file_not_clean` (not uploaded with purpose `receipt`, or not scanned clean yet), `payments.receipt.no_image` or `payments.receipt.image_unavailable` (Instagram's link expired); 409 `payments.receipt.already_submitted`. Images a customer sends while their order awaits payment are submitted automatically.")
            .Produces<ReceiptResponse>(StatusCodes.Status201Created)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapPost("/receipts/{receiptId:guid}/approve", ApproveAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Approve a receipt: its order is paid")
            .WithDescription("For a receipt in review, or one rejected automatically as a duplicate. The order (the receipt's, or `orderId`) moves from `awaiting_payment` to `paid` for its total; otherwise the payment is noted on its timeline. 409 `payments.receipt.not_reviewable`; 422 `payments.receipt.order_required`; 404 `payments.receipt.order_not_found`.")
            .Produces<ReceiptResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .WithStandardProblems();

        ws.MapPost("/receipts/{receiptId:guid}/reject", RejectAsync)
            .RequireWorkspacePermission(Permissions.OrdersWrite)
            .WithSummary("Reject a receipt in review, with a reason (up to 300 characters)")
            .Produces<ReceiptResponse>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithStandardProblems();
    }

    internal static BankAccountResponse ToResponse(BankAccount a) => new(
        a.Id, a.CardNumber, BankCard.Mask(a.CardNumber), a.Sheba, a.HolderName, a.BankCode, Bank(a.BankCode)?.NameFa, a.Enabled, a.CreatedAt);

    internal static ReceiptResponse ToResponse(Receipt r, string? imageUrl = null) => new(
        r.Id, r.Source, r.Status, r.OrderId, r.ContactId, r.ConversationId, r.MessageId, r.FileId, r.Amount is { } a ? Money.Rial(a) : null, r.PaidAt, r.Reference,
        r.SourceCard, r.DestinationCard, r.DestinationSheba, r.Bank, r.Confidence, r.Checks, r.DuplicateOfId, r.RejectReason, r.SubmittedBy, r.DecidedBy, r.DecidedAt,
        r.CreatedAt, imageUrl);

    private static IranianBank? Bank(string? code) => code is null ? null : IranianBanks.ByBin.Values.Concat(IranianBanks.BySheba.Values).FirstOrDefault(b => b.Code == code);

    /// <summary>The cleaned card and Sheba, or the validation error.</summary>
    internal static (string? Card, string? Sheba, string? Bank, IResult? Error) Validate(BankAccountRequest request)
    {
        var hasCard = !string.IsNullOrWhiteSpace(request.CardNumber);
        var card = hasCard ? BankCard.Clean(request.CardNumber) : null;
        if (hasCard && !BankCard.IsValid(card))
        {
            return (null, null, null, Invalid("cardNumber", "payments.bank_account.card_invalid"));
        }

        var hasSheba = !string.IsNullOrWhiteSpace(request.Sheba);
        var sheba = hasSheba ? Sheba.Clean(request.Sheba) : null;
        if (hasSheba && !Sheba.IsValid(sheba))
        {
            return (null, null, null, Invalid("sheba", "payments.bank_account.sheba_invalid"));
        }

        if (card is null && sheba is null)
        {
            return (null, null, null, Invalid("cardNumber", "payments.bank_account.empty"));
        }

        if (request.HolderName is { Length: > 100 })
        {
            return (null, null, null, Invalid("holderName", "validation.too_long"));
        }

        return (card, sheba, (BankCard.GetBank(card) ?? Sheba.GetBank(sheba))?.Code, null);
    }

    private static async Task<IResult> ListAccountsAsync(Guid workspaceId, PaymentsDbContext db, CancellationToken ct) =>
        Results.Ok((await db.BankAccounts.AsNoTracking().OrderBy(a => a.CreatedAt).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> CreateAccountAsync(
        Guid workspaceId, BankAccountRequest request, PaymentsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var (card, sheba, bank, error) = Validate(request);
        if (error is not null)
        {
            return error;
        }

        var existing = await db.BankAccounts.AsNoTracking().ToListAsync(ct);
        if (existing.Count >= BankAccount.MaxPerWorkspace)
        {
            return Error.Conflict("payments.bank_account.limit").ToProblem();
        }

        if (existing.Any(a => (card is not null && a.CardNumber == card) || (sheba is not null && a.Sheba == sheba)))
        {
            return Error.Conflict("payments.bank_account.duplicate").ToProblem();
        }

        var now = clock.UtcNow;
        var account = BankAccount.Create(workspaceId, now);
        account.Update(card, sheba, request.HolderName?.Trim(), bank, request.Enabled, now);
        db.BankAccounts.Add(account);
        Audit(db, caller, "payments.bank_account_added", account, workspaceId, now, after: new { card = BankCard.Mask(card), sheba, request.Enabled });
        await db.SaveChangesAsync(ct);
        return Results.Json(ToResponse(account), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> UpdateAccountAsync(
        Guid workspaceId, Guid accountId, BankAccountRequest request, PaymentsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var (card, sheba, bank, error) = Validate(request);
        if (error is not null)
        {
            return error;
        }

        var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (await db.BankAccounts.AnyAsync(a => a.Id != accountId && ((card != null && a.CardNumber == card) || (sheba != null && a.Sheba == sheba)), ct))
        {
            return Error.Conflict("payments.bank_account.duplicate").ToProblem();
        }

        var now = clock.UtcNow;
        var before = new { card = BankCard.Mask(account.CardNumber), account.Sheba, account.Enabled };
        account.Update(card, sheba, request.HolderName?.Trim(), bank, request.Enabled, now);
        Audit(db, caller, "payments.bank_account_updated", account, workspaceId, now, before, new { card = BankCard.Mask(card), sheba, request.Enabled });
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(account));
    }

    private static async Task<IResult> DeleteAccountAsync(Guid workspaceId, Guid accountId, PaymentsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var account = await db.BankAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (account is not null)
        {
            db.BankAccounts.Remove(account);
            Audit(db, caller, "payments.bank_account_removed", account, workspaceId, clock.UtcNow, before: new { card = BankCard.Mask(account.CardNumber), account.Sheba });
            await db.SaveChangesAsync(ct);
        }

        return Results.NoContent();
    }

    private static ReceiptSettingsResponse ToResponse(ReceiptSettings s, bool available) =>
        new(s.AutoApprove && available, available, s.Threshold, Money.Rial(s.AmountTolerance), s.WindowHours);

    private static async Task<IResult> GetSettingsAsync(Guid workspaceId, PaymentsDbContext db, IOptions<PaymentsOptions> options, CancellationToken ct) =>
        Results.Ok(ToResponse(await db.ReceiptSettings.AsNoTracking().FirstOrDefaultAsync(ct) ?? ReceiptSettings.Default(workspaceId), options.Value.ReceiptAutoApprove));

    private static async Task<IResult> PutSettingsAsync(
        Guid workspaceId, ReceiptSettingsRequest request, PaymentsDbContext db, IOptions<PaymentsOptions> options, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        if (request.Threshold is < ReceiptSettings.MinThreshold or > 1 || double.IsNaN(request.Threshold))
        {
            return Invalid("threshold", "payments.receipts.threshold_invalid");
        }

        if (request.AmountTolerance is < 0 or > ReceiptSettings.MaxTolerance)
        {
            return Invalid("amountTolerance", "payments.receipts.tolerance_invalid");
        }

        if (request.WindowHours is < 1 or > ReceiptSettings.MaxWindowHours)
        {
            return Invalid("windowHours", "payments.receipts.window_invalid");
        }

        if (request.AutoApprove && !options.Value.ReceiptAutoApprove)
        {
            return Error.Unprocessable("payments.receipts.auto_approve_unavailable").ToProblem();
        }

        var now = clock.UtcNow;
        var settings = await db.ReceiptSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = ReceiptSettings.Default(workspaceId);
            db.ReceiptSettings.Add(settings);
        }

        var before = new { settings.AutoApprove, settings.Threshold, settings.AmountTolerance, settings.WindowHours };
        settings.AutoApprove = request.AutoApprove;
        settings.Threshold = request.Threshold;
        settings.AmountTolerance = request.AmountTolerance;
        settings.WindowHours = request.WindowHours;
        settings.UpdatedAt = now;
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), "payments.receipt_settings_updated", "receipt_settings", workspaceId.ToString(), workspaceId, now,
            before: before, after: new { request.AutoApprove, request.Threshold, request.AmountTolerance, request.WindowHours });
        db.Publish(entry, entry.PartitionKey(), workspaceId);
        await db.SaveChangesAsync(ct);
        return Results.Ok(ToResponse(settings, options.Value.ReceiptAutoApprove));
    }

    private static async Task<IResult> ListAsync(Guid workspaceId, string? status, Guid? orderId, string? cursor, PaymentsDbContext db, CancellationToken ct)
    {
        if (status is not null && !ReceiptStatuses.All.Contains(status))
        {
            return Invalid("status", "payments.receipts.status_invalid");
        }

        var query = db.Receipts.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(r => r.Status == status);
        }

        if (orderId is { } o)
        {
            query = query.Where(r => r.OrderId == o);
        }

        if (cursor is not null)
        {
            if (Cursor(cursor) is not { } after)
            {
                return Invalid("cursor", "validation.cursor_invalid");
            }

            var (at, id) = after;
            query = query.Where(r => EF.Functions.LessThan(ValueTuple.Create(r.CreatedAt, r.Id), ValueTuple.Create(at, id)));
        }

        var rows = await query.OrderByDescending(r => r.CreatedAt).ThenByDescending(r => r.Id).Take(PageSize + 1).ToListAsync(ct);
        var more = rows.Count > PageSize;
        var page = rows.Take(PageSize).ToList();
        return Results.Ok(new ReceiptPage([.. page.Select(r => ToResponse(r))], more ? $"{page[^1].CreatedAt.ToUnixTimeMilliseconds()}_{page[^1].Id:N}" : null));
    }

    /// <summary><c>{unix milliseconds}_{id}</c> of the last receipt of the previous page.</summary>
    private static (DateTimeOffset At, Guid Id)? Cursor(string cursor)
    {
        var parts = cursor.Split('_');
        return parts.Length == 2 && long.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ms)
            && ms is > 0 and < 32_503_680_000_000 && Guid.TryParseExact(parts[1], "N", out var id)
            ? (DateTimeOffset.FromUnixTimeMilliseconds(ms), id)
            : null;
    }

    private static async Task<IResult> GetAsync(Guid workspaceId, Guid receiptId, PaymentsDbContext db, IFileService files, CancellationToken ct)
    {
        var receipt = await db.Receipts.AsNoTracking().FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
        {
            return Error.NotFound().ToProblem();
        }

        string? url = null;
        if (receipt.FileId is { } fileId && (await files.GetCleanUrlsAsync([fileId], TimeSpan.FromMinutes(10), ct)).TryGetValue(fileId, out var signed))
        {
            url = signed.ToString();
        }

        return Results.Ok(ToResponse(receipt, url));
    }

    private static async Task<IResult> SubmitAsync(
        Guid workspaceId, SubmitReceiptRequest request, ReceiptSubmission submission, ICurrentUser caller, CancellationToken ct)
    {
        if ((request.FileId is null) == (request.MessageId is null))
        {
            return Invalid("fileId", "payments.receipt.file_or_message");
        }

        var (receipt, error) = await submission.SubmitAsync(workspaceId, request.FileId, request.MessageId, request.OrderId, caller.UserId, ct);
        return error is not null ? error.ToProblem() : Results.Json(ToResponse(receipt!), statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> ApproveAsync(
        Guid workspaceId, Guid receiptId, ApproveReceiptRequest? request, PaymentsDbContext db, IOrderQueries orders, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (!receipt.CanApprove)
        {
            return Error.Conflict("payments.receipt.not_reviewable").ToProblem();
        }

        if ((request?.OrderId ?? receipt.OrderId) is not { } orderId)
        {
            return Error.Unprocessable("payments.receipt.order_required").ToProblem();
        }

        var order = await orders.GetForPaymentAsync(orderId, ct);
        if (order is null)
        {
            return Error.NotFound("payments.receipt.order_not_found").ToProblem();
        }

        var now = clock.UtcNow;
        var before = new { receipt.Status, receipt.Amount, receipt.OrderId };
        receipt.Approve(order.Id, caller.UserId, now);

        // The seller checked the transfer: the order is paid for its total (the Orders module only notes it if the order moved on).
        ReceiptNotices.Confirm(db, receipt, order.Id, order.Total, now);
        Audit(db, caller, "payments.receipt_approved", receipt, workspaceId, now, before, new { receipt.Status, orderId = order.Id, total = order.Total.Amount });
        return await SaveAsync(db, receipt, ct);
    }

    private static async Task<IResult> RejectAsync(
        Guid workspaceId, Guid receiptId, RejectReceiptRequest request, PaymentsDbContext db, ICurrentUser caller, IClock clock, CancellationToken ct)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > Receipt.MaxReason)
        {
            return Invalid("reason", "payments.receipt.reason_invalid");
        }

        var receipt = await db.Receipts.FirstOrDefaultAsync(r => r.Id == receiptId, ct);
        if (receipt is null)
        {
            return Error.NotFound().ToProblem();
        }

        if (receipt.Status != ReceiptStatuses.NeedsReview)
        {
            return Error.Conflict("payments.receipt.not_reviewable").ToProblem();
        }

        var now = clock.UtcNow;
        receipt.Reject(reason, caller.UserId, now);
        Audit(db, caller, "payments.receipt_rejected", receipt, workspaceId, now, new { Status = ReceiptStatuses.NeedsReview }, new { receipt.Status, reason });
        return await SaveAsync(db, receipt, ct);
    }

    private static async Task<IResult> SaveAsync(PaymentsDbContext db, Receipt receipt, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Decided by someone else at the same time.
            return Error.Conflict("payments.receipt.not_reviewable").ToProblem();
        }

        return Results.Ok(ToResponse(receipt));
    }

    private static void Audit(
        PaymentsDbContext db, ICurrentUser caller, string action, BankAccount account, Guid workspaceId, DateTimeOffset now, object? before = null, object? after = null)
    {
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), action, "bank_account", account.Id.ToString(), workspaceId, now, before: before, after: after);
        db.Publish(entry, entry.PartitionKey(), workspaceId);
    }

    private static void Audit(PaymentsDbContext db, ICurrentUser caller, string action, Receipt receipt, Guid workspaceId, DateTimeOffset now, object before, object after)
    {
        var entry = AuditEntries.Create(
            AuditActor.User(caller.UserId, caller.ActorStaffId, caller.IpAddress), action, "receipt", receipt.Id.ToString(), workspaceId, now, before: before, after: after);
        db.Publish(entry, entry.PartitionKey(), workspaceId);
    }

    private static IResult Invalid(string field, string code) =>
        Error.Validation(code, new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [code] }).ToProblem();
}
