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
}

public sealed record UserSummary(Guid Id, string? DisplayName, string MaskedPhone);
