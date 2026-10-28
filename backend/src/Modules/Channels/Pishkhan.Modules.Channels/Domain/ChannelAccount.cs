using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Channels.Domain;

/// <summary>A connected Instagram professional account (or, later, a Bale bot, SMS sender, web widget, sandbox).</summary>
public sealed class ChannelAccount : AggregateRoot<Guid>, ITenantOwned
{
    private ChannelAccount()
    {
    }

    public Guid WorkspaceId { get; private set; }

    public string Kind { get; private set; } = ChannelKinds.Instagram;

    public string ExternalId { get; private set; } = string.Empty;

    public string? Username { get; private set; }

    public string? DisplayName { get; private set; }

    public string Status { get; private set; } = ChannelStatuses.Connecting;

    public string? TokenEnc { get; private set; }

    public DateTimeOffset? TokenExpiresAt { get; private set; }

    public List<string> Scopes { get; private set; } = [];

    /// <summary>Name of the rate profile (editable in admin because Meta limits change).</summary>
    public string RateProfile { get; private set; } = "default";

    public Guid? ConnectedBy { get; private set; }

    public static ChannelAccount ConnectInstagram(Guid workspaceId, string externalId, string? username, string? name, string tokenEnc, DateTimeOffset tokenExpiresAt, IEnumerable<string> scopes, Guid connectedBy, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        WorkspaceId = workspaceId,
        Kind = ChannelKinds.Instagram,
        ExternalId = externalId,
        Username = username,
        DisplayName = name,
        TokenEnc = tokenEnc,
        TokenExpiresAt = tokenExpiresAt,
        Scopes = [.. scopes],
        Status = ChannelStatuses.Active,
        ConnectedBy = connectedBy,
        CreatedAt = now,
    };

    public void Reconnect(string? username, string? name, string tokenEnc, DateTimeOffset tokenExpiresAt, IEnumerable<string> scopes)
    {
        Username = username;
        DisplayName = name;
        TokenEnc = tokenEnc;
        TokenExpiresAt = tokenExpiresAt;
        Scopes = [.. scopes];
        Status = ChannelStatuses.Active;
    }

    public void RequireReauth() => Status = ChannelStatuses.ReauthRequired;

    public void Disconnect()
    {
        Status = ChannelStatuses.Disconnected;
        TokenEnc = null;
    }
}

/// <summary>A data deletion request from Meta (required callback). Processing is audited and its status is public by code.</summary>
public sealed class DataDeletionRequest
{
    public Guid Id { get; set; }

    public required string ConfirmationCode { get; set; }

    public required string ExternalUserId { get; set; }

    public string Status { get; set; } = "received";

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
