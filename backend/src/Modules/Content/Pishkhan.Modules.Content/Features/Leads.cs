using System.Text.RegularExpressions;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Content.Contracts;
using Pishkhan.Modules.Content.Domain;
using Pishkhan.Modules.Content.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;

namespace Pishkhan.Modules.Content.Features;

/// <summary>A website form submission. <see cref="Altcha"/> is the solved challenge from <c>GET /altcha/challenge</c> (single use).</summary>
public sealed record LeadRequest(
    string Type, string Phone, string? Name, string? InstagramHandle, string? Vertical, string? MonthlyDms, string? Message,
    Dictionary<string, string>? Utm, string? Altcha);

public sealed record LeadAccepted(string Status);

public sealed record ListLeadsQuery(string? Type, int? Page, int? PageSize);

public sealed record LeadResponse(
    Guid Id, string Type, string Phone, string? Name, string? InstagramHandle, string? Vertical, string? MonthlyDms, string? Message,
    IReadOnlyDictionary<string, string> Utm, int Submissions, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

internal static partial class LeadRules
{
    public const int MaxMessage = 2100;
    public const int MaxUtmKeys = 10;

    [GeneratedRegex("^@?[A-Za-z0-9._]{1,30}$")]
    public static partial Regex InstagramHandle();

    [GeneratedRegex("^(utm_[a-z]{1,20}|ref)$")]
    public static partial Regex UtmKey();

    public static string? Text(string? value, int max)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed[..Math.Min(trimmed.Length, max)];
    }

    public static string? Handle(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().TrimStart('@').ToLowerInvariant();
}

internal sealed class LeadRequestValidator : AbstractValidator<LeadRequest>
{
    public LeadRequestValidator()
    {
        RuleFor(x => x.Type).Must(t => LeadTypes.All.Contains(t)).WithErrorCode("validation.invalid");
        RuleFor(x => x.Phone).NotEmpty().WithErrorCode("validation.required").Must(p => PhoneNumber.TryParse(p, out _)).WithErrorCode("validation.phone.invalid");
        RuleFor(x => x.Name).MaximumLength(100).WithErrorCode("validation.too_long");
        RuleFor(x => x.InstagramHandle).Must(h => string.IsNullOrWhiteSpace(h) || LeadRules.InstagramHandle().IsMatch(h.Trim())).WithErrorCode("validation.instagram.invalid");
        RuleFor(x => x.Vertical).MaximumLength(40).WithErrorCode("validation.too_long");
        RuleFor(x => x.MonthlyDms).MaximumLength(40).WithErrorCode("validation.too_long");
        RuleFor(x => x.Message).MaximumLength(LeadRules.MaxMessage).WithErrorCode("validation.too_long");
        RuleFor(x => x.Altcha).MaximumLength(2000).WithErrorCode("validation.too_long");
        RuleFor(x => x.Utm)
            .Must(u => u is null || (u.Count <= LeadRules.MaxUtmKeys && u.All(kv => LeadRules.UtmKey().IsMatch(kv.Key) && kv.Value.Length <= 200)))
            .WithErrorCode("validation.invalid");

        // Same rules as the website's form: the waitlist needs the Instagram page, the contact form a name and a message.
        When(x => x.Type == LeadTypes.Waitlist, () =>
            RuleFor(x => x.InstagramHandle).NotEmpty().WithErrorCode("validation.required"));
        When(x => x.Type == LeadTypes.Contact, () =>
        {
            RuleFor(x => x.Name).NotEmpty().WithErrorCode("validation.required");
            RuleFor(x => x.Message).NotEmpty().WithErrorCode("validation.required");
        });
    }
}

internal sealed class LeadHandlers(ContentDbContext db, ISecretProtector secrets, IBlindIndexer indexer, AltchaVerifier altcha, ICurrentUser caller, IClock clock) :
    ICommandHandler<LeadRequest, LeadAccepted>,
    IQueryHandler<ListLeadsQuery, PagedResult<LeadResponse>>
{
    internal const string PhonePurpose = "content.lead.phone";

    public async Task<Result<LeadAccepted>> HandleAsync(LeadRequest command, CancellationToken cancellationToken)
    {
        if (!await altcha.VerifyAsync(command.Altcha))
        {
            return Error.Validation("validation.altcha.invalid", new Dictionary<string, string[]>(StringComparer.Ordinal) { ["altcha"] = ["validation.altcha.invalid"] });
        }

        var now = clock.UtcNow;
        var phone = PhoneNumber.Parse(command.Phone);
        var bidx = indexer.Compute(phone.E164, PhonePurpose);
        var details = new LeadDetails(
            LeadRules.Text(command.Name, 100), LeadRules.Handle(command.InstagramHandle), LeadRules.Text(command.Vertical, 40),
            LeadRules.Text(command.MonthlyDms, 40), LeadRules.Text(command.Message, LeadRules.MaxMessage),
            command.Utm?.ToDictionary(kv => kv.Key, kv => kv.Value.Trim(), StringComparer.Ordinal) ?? []);

        // A repeat within a day (double click, a second visit) updates the lead. The answer is the same either way.
        var since = now - Lead.MergeWindow;
        var existing = await db.Leads.Where(l => l.PhoneBidx == bidx && l.Type == command.Type && l.CreatedAt >= since)
            .OrderByDescending(l => l.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (existing is null)
        {
            db.Leads.Add(Lead.Submit(command.Type, secrets.Protect(phone.E164, PhonePurpose), bidx, details, caller.IpAddress, now));
        }
        else
        {
            existing.Resubmit(details, caller.IpAddress, now);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new LeadAccepted("received");
    }

    public async Task<Result<PagedResult<LeadResponse>>> HandleAsync(ListLeadsQuery query, CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page ?? 1);
        var pageSize = Math.Clamp(query.PageSize ?? 25, 1, 100);
        var leads = db.Leads.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(query.Type))
        {
            leads = leads.Where(l => l.Type == query.Type);
        }

        var total = await leads.CountAsync(cancellationToken);
        var rows = await leads.OrderByDescending(l => l.CreatedAt).ThenByDescending(l => l.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        var items = rows.Select(l => new LeadResponse(
            l.Id, l.Type, PhoneNumber.Parse(secrets.Unprotect(l.PhoneEnc, PhonePurpose)).National, l.Name, l.InstagramHandle, l.Vertical, l.MonthlyDms, l.Message,
            l.Utm, l.Submissions, l.CreatedAt, l.UpdatedAt)).ToList();
        return new PagedResult<LeadResponse>(items, total, page, pageSize);
    }
}
