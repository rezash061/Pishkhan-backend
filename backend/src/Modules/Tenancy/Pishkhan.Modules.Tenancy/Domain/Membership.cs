using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Tenancy.Domain;

public sealed class Membership : Entity<Guid>, ITenantOwned
{
    private Membership()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public Guid UserId { get; private set; }

    public string Role { get; private set; } = WorkspaceRoles.Viewer;

    /// <summary>Extra permissions granted on top of the role (custom roles arrive in Phase 3).</summary>
    public List<string> PermissionsOverride { get; private set; } = [];

    public static Membership Create(Guid workspaceId, Guid userId, string role, DateTimeOffset now)
    {
        if (!WorkspaceRoles.IsValid(role))
        {
            throw new DomainException("tenancy.role.invalid");
        }

        return new Membership { Id = Uuid.NewV7(now), WorkspaceId = workspaceId, UserId = userId, Role = role, CreatedAt = now };
    }

    public void ChangeRole(string role)
    {
        if (!WorkspaceRoles.IsValid(role))
        {
            throw new DomainException("tenancy.role.invalid");
        }

        Role = role;
    }

    public IReadOnlySet<string> EffectivePermissions() =>
        WorkspaceRoles.PermissionsOf(Role).Union(PermissionsOverride.Where(p => Permissions.All.Contains(p))).ToHashSet(StringComparer.Ordinal);
}

public enum InvitationStatus
{
    Pending,
    Accepted,
    Revoked,
}

/// <summary>An invitation by phone number. The SMS carries a one-time token; only the user with that phone can accept it.</summary>
public sealed class Invitation : Entity<Guid>, ITenantOwned
{
    private Invitation()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string PhoneEnc { get; private set; } = string.Empty;

    public string PhoneBidx { get; private set; } = string.Empty;

    public string Role { get; private set; } = WorkspaceRoles.Agent;

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public InvitationStatus Status { get; private set; }

    public Guid InvitedBy { get; private set; }

    public Guid? AcceptedBy { get; private set; }

    public static Invitation Create(Guid workspaceId, string phoneEnc, string phoneBidx, string role, string tokenHash, Guid invitedBy, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        PhoneEnc = phoneEnc,
        PhoneBidx = phoneBidx,
        Role = role,
        TokenHash = tokenHash,
        InvitedBy = invitedBy,
        ExpiresAt = now.AddDays(7),
        Status = InvitationStatus.Pending,
        CreatedAt = now,
    };

    public void Accept(Guid userId)
    {
        Status = InvitationStatus.Accepted;
        AcceptedBy = userId;
    }

    public void Revoke() => Status = InvitationStatus.Revoked;
}
