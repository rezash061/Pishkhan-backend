using System.Security.Cryptography;
using System.Text;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.Modules.Tenancy.Domain;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Tenancy.Features;

public sealed record MemberResponse(Guid UserId, string? DisplayName, string? Phone, string Role, DateTimeOffset JoinedAt);

public sealed record ChangeRoleRequest(string Role);

public sealed record InviteRequest(string Phone, string Role);

public sealed record InvitationResponse(Guid Id, string Role, DateTimeOffset ExpiresAt, string Status);

public sealed record AcceptInvitationResponse(Guid WorkspaceId, string Role);

public sealed record ListMembersQuery;

public sealed record ChangeRoleCommand(Guid UserId, string Role);

public sealed record RemoveMemberCommand(Guid UserId);

public sealed record AcceptInvitationCommand(string Token);

public sealed class TenancyOptions
{
    public const string Section = "Tenancy";

    /// <summary>Base URL of the panel's invitation page; the token is appended.</summary>
    public string InvitationUrl { get; set; } = "https://app.example.ir/invite/";
}

internal sealed class ChangeRoleValidator : AbstractValidator<ChangeRoleCommand>
{
    public ChangeRoleValidator() => RuleFor(x => x.Role).Must(WorkspaceRoles.IsValid).WithErrorCode("validation.role.invalid");
}

internal sealed class InviteValidator : AbstractValidator<InviteRequest>
{
    public InviteValidator()
    {
        RuleFor(x => x.Phone).Must(p => PhoneNumber.TryParse(p, out _)).WithErrorCode("validation.phone.invalid");
        RuleFor(x => x.Role).Must(r => WorkspaceRoles.IsValid(r) && r != WorkspaceRoles.Owner).WithErrorCode("validation.role.invalid");
    }
}

internal sealed class MemberHandlers(
    TenancyDbContext db,
    ITenantContext tenant,
    ICurrentUser caller,
    IUserDirectory users,
    ISecretProtector secrets,
    ISmsSender sms,
    ICacheInvalidator cache,
    IOptions<TenancyOptions> options,
    IClock clock) :
    IQueryHandler<ListMembersQuery, IReadOnlyList<MemberResponse>>,
    ICommandHandler<ChangeRoleCommand, MemberResponse>,
    ICommandHandler<RemoveMemberCommand, Unit>,
    ICommandHandler<InviteRequest, InvitationResponse>,
    ICommandHandler<AcceptInvitationCommand, AcceptInvitationResponse>
{
    private const string PhonePurpose = "tenancy.invitation.phone";

    public async Task<Result<IReadOnlyList<MemberResponse>>> HandleAsync(ListMembersQuery query, CancellationToken cancellationToken)
    {
        var members = await db.Memberships.AsNoTracking().OrderBy(m => m.CreatedAt).ToListAsync(cancellationToken);
        var result = new List<MemberResponse>(members.Count);
        foreach (var m in members)
        {
            var user = await users.GetAsync(m.UserId, cancellationToken);
            result.Add(new MemberResponse(m.UserId, user?.DisplayName, user?.MaskedPhone, m.Role, m.CreatedAt));
        }

        return result;
    }

    public async Task<Result<MemberResponse>> HandleAsync(ChangeRoleCommand command, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == command.UserId, cancellationToken);
        if (membership is null)
        {
            return Error.NotFound();
        }

        var callerRole = await db.Memberships.Where(m => m.UserId == caller.UserId).Select(m => m.Role).FirstAsync(cancellationToken);
        if ((command.Role == WorkspaceRoles.Owner || membership.Role == WorkspaceRoles.Owner) && callerRole != WorkspaceRoles.Owner)
        {
            return Error.Forbidden("tenancy.owner_only");
        }

        if (membership.Role == WorkspaceRoles.Owner && command.Role != WorkspaceRoles.Owner && await IsLastOwnerAsync(membership, cancellationToken))
        {
            return Error.Conflict("tenancy.last_owner");
        }

        membership.ChangeRole(command.Role);
        await SaveMembershipChangeAsync(membership.UserId, command.Role, removed: false, cancellationToken);
        var user = await users.GetAsync(membership.UserId, cancellationToken);
        return new MemberResponse(membership.UserId, user?.DisplayName, user?.MaskedPhone, membership.Role, membership.CreatedAt);
    }

    public async Task<Result<Unit>> HandleAsync(RemoveMemberCommand command, CancellationToken cancellationToken)
    {
        var membership = await db.Memberships.FirstOrDefaultAsync(m => m.UserId == command.UserId, cancellationToken);
        if (membership is null)
        {
            return Error.NotFound();
        }

        if (membership.Role == WorkspaceRoles.Owner && await IsLastOwnerAsync(membership, cancellationToken))
        {
            return Error.Conflict("tenancy.last_owner");
        }

        db.Memberships.Remove(membership);
        await SaveMembershipChangeAsync(membership.UserId, null, removed: true, cancellationToken);
        return Unit.Value;
    }

    public async Task<Result<InvitationResponse>> HandleAsync(InviteRequest command, CancellationToken cancellationToken)
    {
        var phone = PhoneNumber.Parse(command.Phone);
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(24));
        var invitation = Invitation.Create(
            tenant.WorkspaceId!.Value,
            secrets.Protect(phone.E164, PhonePurpose),
            users.PhoneBlindIndex(phone),
            command.Role,
            Hash(token),
            caller.UserId,
            clock.UtcNow);
        db.Invitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);

        var workspaceName = await db.Workspaces.Where(w => w.Id == invitation.WorkspaceId).Select(w => w.Name).FirstAsync(cancellationToken);
        await sms.SendAsync(new SmsMessage(phone, $"شما به «{workspaceName}» در پیشخوان دعوت شده‌اید:\n{options.Value.InvitationUrl}{token}"), cancellationToken);
        return new InvitationResponse(invitation.Id, invitation.Role, invitation.ExpiresAt, "pending");
    }

    public async Task<Result<AcceptInvitationResponse>> HandleAsync(AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var hash = Hash(command.Token);
        var invitation = await db.Invitations.IgnoreQueryFilters([ModuleDbContext.TenantFilter]).FirstOrDefaultAsync(i => i.TokenHash == hash, cancellationToken);
        if (invitation is null || invitation.Status != InvitationStatus.Pending || invitation.ExpiresAt < clock.UtcNow)
        {
            return Error.NotFound("tenancy.invitation.invalid");
        }

        // Only the invited phone may accept: possession of the link alone is not enough.
        if (!string.Equals(await users.GetPhoneBlindIndexAsync(caller.UserId, cancellationToken), invitation.PhoneBidx, StringComparison.Ordinal))
        {
            return Error.Forbidden("tenancy.invitation.wrong_user");
        }

        tenant.SetWorkspace(invitation.WorkspaceId);
        invitation.Accept(caller.UserId);
        if (!await db.Memberships.AnyAsync(m => m.UserId == caller.UserId, cancellationToken))
        {
            db.Memberships.Add(Membership.Create(invitation.WorkspaceId, caller.UserId, invitation.Role, clock.UtcNow));
        }

        await SaveMembershipChangeAsync(caller.UserId, invitation.Role, removed: false, cancellationToken);
        return new AcceptInvitationResponse(invitation.WorkspaceId, invitation.Role);
    }

    private async Task<bool> IsLastOwnerAsync(Membership membership, CancellationToken cancellationToken) =>
        !await db.Memberships.AnyAsync(m => m.Role == WorkspaceRoles.Owner && m.UserId != membership.UserId, cancellationToken);

    private async Task SaveMembershipChangeAsync(Guid userId, string? role, bool removed, CancellationToken cancellationToken)
    {
        var workspaceId = tenant.WorkspaceId!.Value;
        db.Publish(new MembershipChanged(workspaceId, userId, role, removed), key: workspaceId.ToString());
        var tag = CacheTags.Member(workspaceId, userId);
        cache.Enqueue(db, tag);
        await db.SaveChangesAsync(cancellationToken);
        await cache.InvalidateLocalAsync([tag], cancellationToken);
    }

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
