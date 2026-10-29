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

    public DateTimeOffset? TokenRefreshedAt { get; private set; }

    /// <summary>Last refresh failure (error code), cleared on success. Shown to support staff.</summary>
    public string? LastRefreshError { get; private set; }

    public int RefreshFailures { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset? StatusChangedAt { get; private set; }

    /// <summary>
    /// Allowed status moves (backend plan 9.3): <c>connecting → active → token_expiring → reauth_required | disconnected</c>.
    /// Reconnecting brings any status back to <c>active</c>; a successful refresh brings <c>token_expiring</c> back.
    /// </summary>
    public static bool CanMove(string from, string to) => (from, to) switch
    {
        _ when from == to => false,
        (_, ChannelStatuses.Disconnected) => true,
        (_, ChannelStatuses.Active) => true,
        (ChannelStatuses.Active, ChannelStatuses.TokenExpiring) => true,
        (ChannelStatuses.Connecting or ChannelStatuses.Active or ChannelStatuses.TokenExpiring, ChannelStatuses.ReauthRequired) => true,
        _ => false,
    };

    public static ChannelAccount ConnectInstagram(Guid workspaceId, string externalId, string? username, string? name, string tokenEnc, DateTimeOffset tokenExpiresAt, IEnumerable<string> scopes, Guid connectedBy, DateTimeOffset now)
    {
        var account = new ChannelAccount
        {
            Id = Uuid.NewV7(now),
            WorkspaceId = workspaceId,
            Kind = ChannelKinds.Instagram,
            ExternalId = externalId,
            Username = username,
            DisplayName = name,
            TokenEnc = tokenEnc,
            TokenExpiresAt = tokenExpiresAt,
            TokenRefreshedAt = now,
            Scopes = [.. scopes],
            ConnectedBy = connectedBy,
            CreatedAt = now,
        };
        account.MoveTo(ChannelStatuses.Active, "connected", now);
        return account;
    }

    public bool Reconnect(string? username, string? name, string tokenEnc, DateTimeOffset tokenExpiresAt, IEnumerable<string> scopes, DateTimeOffset now)
    {
        Username = username;
        DisplayName = name;
        TokenEnc = tokenEnc;
        TokenExpiresAt = tokenExpiresAt;
        TokenRefreshedAt = now;
        Scopes = [.. scopes];
        ClearRefreshFailures();
        return MoveTo(ChannelStatuses.Active, "reconnected", now);
    }

    /// <summary>A long-lived token can be refreshed once it is 24 hours old and before it expires.</summary>
    public bool IsDueForRefresh(DateTimeOffset now, TimeSpan refreshBefore) =>
        Status is ChannelStatuses.Active or ChannelStatuses.TokenExpiring
        && TokenEnc is not null
        && TokenExpiresAt is { } expires && expires > now && expires - now <= refreshBefore
        && (TokenRefreshedAt ?? CreatedAt) <= now.AddHours(-24);

    public bool TokenRefreshed(string tokenEnc, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        TokenEnc = tokenEnc;
        TokenExpiresAt = expiresAt;
        TokenRefreshedAt = now;
        ClearRefreshFailures();
        return Status == ChannelStatuses.TokenExpiring && MoveTo(ChannelStatuses.Active, "token_refreshed", now);
    }

    /// <summary>A transient refresh failure. Returns true when the status changed (the owner should be told).</summary>
    public bool TokenRefreshFailed(string errorCode, DateTimeOffset now, TimeSpan expiringWarning)
    {
        LastRefreshError = errorCode;
        RefreshFailures++;
        if (TokenExpiresAt is { } expires && expires <= now)
        {
            return MoveTo(ChannelStatuses.ReauthRequired, "token_expired", now);
        }

        return TokenExpiresAt is { } e && e - now <= expiringWarning && MoveTo(ChannelStatuses.TokenExpiring, errorCode, now);
    }

    public bool RequireReauth(string reason, DateTimeOffset now) => MoveTo(ChannelStatuses.ReauthRequired, reason, now);

    public bool Disconnect(string reason, DateTimeOffset now)
    {
        TokenEnc = null;
        return MoveTo(ChannelStatuses.Disconnected, reason, now);
    }

    public void UseRateProfile(string name) => RateProfile = name;

    private void ClearRefreshFailures()
    {
        LastRefreshError = null;
        RefreshFailures = 0;
    }

    private bool MoveTo(string status, string reason, DateTimeOffset now)
    {
        if (!CanMove(Status, status))
        {
            return false;
        }

        Status = status;
        StatusReason = reason;
        StatusChangedAt = now;
        return true;
    }
}

/// <summary>Admin override of a rate profile (Meta limits change without notice). Absent names fall back to configuration.</summary>
public sealed class RateProfileOverride
{
    public required string Name { get; set; }

    public required RateProfileValues Values { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public Guid? UpdatedBy { get; set; }
}

public sealed record RateProfileValues(RateBucketValues Send, RateBucketValues PrivateReply, RateBucketValues CommentReply, RateBucketValues Read);

public sealed record RateBucketValues(int Capacity, double RefillPerSecond);

/// <summary>A data deletion request from Meta (required callback). Processing is audited and its status is public by code.</summary>
public sealed class DataDeletionRequest
{
    /// <summary>Modules that hold customer data and must confirm deletion. Grows with CRM, Orders and Payments.</summary>
    public static readonly IReadOnlyList<string> RequiredModules = ["conversations"];

    public Guid Id { get; set; }

    public required string ConfirmationCode { get; set; }

    public required string ExternalUserId { get; set; }

    public string Status { get; set; } = DataDeletionStatuses.Received;

    public List<string> CompletedModules { get; set; } = [];

    public int ItemsDeleted { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Records one module's confirmation. Returns false when it was already recorded (redelivery).</summary>
    public bool ModuleCompleted(string module, int items, DateTimeOffset now)
    {
        if (CompletedModules.Contains(module))
        {
            return false;
        }

        CompletedModules = [.. CompletedModules, module];
        ItemsDeleted += items;
        Status = RequiredModules.All(CompletedModules.Contains) ? DataDeletionStatuses.Completed : DataDeletionStatuses.Processing;
        UpdatedAt = now;
        return true;
    }
}

public static class DataDeletionStatuses
{
    public const string Received = "received";
    public const string Processing = "processing";
    public const string Completed = "completed";
}
