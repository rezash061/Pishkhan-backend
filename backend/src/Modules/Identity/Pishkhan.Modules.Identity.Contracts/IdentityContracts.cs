using Pishkhan.Contracts;

namespace Pishkhan.Modules.Identity.Contracts;

[IntegrationEvent(Topics.UserRegistered)]
public sealed record UserRegistered(Guid UserId, DateTimeOffset RegisteredAt) : IIntegrationEvent;

/// <summary>In-process queries other modules may use.</summary>
public interface IUserDirectory
{
    /// <summary>The blind index of the user's phone (for matching invitations), or null.</summary>
    Task<string?> GetPhoneBlindIndexAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>Blind index of a phone number, computed the same way as stored ones.</summary>
    string PhoneBlindIndex(SharedKernel.PhoneNumber phone);

    Task<UserSummary?> GetAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user's phone, decrypted, for sending them an SMS. Never return it in an API response.</summary>
    Task<SharedKernel.PhoneNumber?> GetPhoneAsync(Guid userId, CancellationToken cancellationToken);

    /// <summary>The user registered with this phone (matched by its blind index), or null. Back office search.</summary>
    Task<Guid?> FindByPhoneAsync(SharedKernel.PhoneNumber phone, CancellationToken cancellationToken);

    /// <summary>Several users at once, for back office lists. Unknown ids are left out.</summary>
    Task<IReadOnlyDictionary<Guid, UserSummary>> GetManyAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken);
}

public sealed record UserSummary(Guid Id, string? DisplayName, string MaskedPhone);

/// <summary>
/// A support session (backend plan 3.4, impersonation): a staff member, after step-up and with a reason, gets a one-time token that the
/// user panel exchanges for a 30-minute access token as a member of the workspace, carrying the staff member in the <c>act</c> claim.
/// The back office writes the grant to the state Redis under <see cref="SupportSessions.GrantKey"/>; the Api takes it once.
/// </summary>
public sealed record SupportSessionGrant(Guid StaffId, string StaffUsername, Guid WorkspaceId, Guid? UserId, string Reason, DateTimeOffset CreatedAt);

public static class SupportSessions
{
    /// <summary>How long the one-time link works.</summary>
    public static readonly TimeSpan GrantLifetime = TimeSpan.FromMinutes(2);

    /// <summary>How long the support session lasts; it cannot be refreshed.</summary>
    public static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>Redis key (area, rest) of a grant: the SHA-256 of the token, never the token itself.</summary>
    public static (string Area, string Name) GrantKey(string tokenHash) => ("support-grant", tokenHash);

    /// <summary>Claim with the staff member's username, for the panel's banner.</summary>
    public const string ActorNameClaim = "act_name";
}
