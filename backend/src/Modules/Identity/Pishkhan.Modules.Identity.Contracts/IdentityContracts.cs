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
