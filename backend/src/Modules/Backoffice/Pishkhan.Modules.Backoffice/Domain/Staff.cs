using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Backoffice.Domain;

public enum StaffStatus
{
    Active,
    Disabled,
}

/// <summary>A back-office user. Separate store from panel users; password plus mandatory TOTP (backend plan 3.4).</summary>
public sealed class StaffUser : AggregateRoot<Guid>
{
    private StaffUser()
    {
    }

    public string Username { get; private set; } = string.Empty;

    public string PasswordHash { get; private set; } = string.Empty;

    public string? TotpSecretEnc { get; private set; }

    public bool TotpEnabled { get; private set; }

    public long? LastTotpStep { get; private set; }

    public List<string> Roles { get; private set; } = [];

    public StaffStatus Status { get; private set; }

    public int FailedLogins { get; private set; }

    public DateTimeOffset? LockedUntil { get; private set; }

    /// <summary>SHA-256 hashes of the unused one-time recovery codes (admin plan AD-0.2), given out at TOTP enrollment.</summary>
    public List<string> RecoveryCodeHashes { get; private set; } = [];

    public static StaffUser Create(string username, string passwordHash, IEnumerable<string> roles, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        Username = username.Trim().ToLowerInvariant(),
        PasswordHash = passwordHash,
        Roles = [.. roles.Distinct(StringComparer.Ordinal)],
        Status = StaffStatus.Active,
        CreatedAt = now,
    };

    public bool IsLocked(DateTimeOffset now) => LockedUntil is { } until && until > now;

    public void RecordFailure(DateTimeOffset now)
    {
        FailedLogins++;
        if (FailedLogins >= 5)
        {
            LockedUntil = now.AddMinutes(15);
            FailedLogins = 0;
        }
    }

    public void RecordSuccess()
    {
        FailedLogins = 0;
        LockedUntil = null;
    }

    public void SetTotpSecret(string secretEnc)
    {
        if (TotpEnabled)
        {
            throw new DomainException("backoffice.totp.already_enabled");
        }

        TotpSecretEnc = secretEnc;
    }

    public void EnableTotp(long step)
    {
        TotpEnabled = true;
        LastTotpStep = step;
    }

    public bool AcceptTotpStep(long step)
    {
        if (LastTotpStep is { } last && step <= last)
        {
            return false;
        }

        LastTotpStep = step;
        return true;
    }

    public void SetRecoveryCodes(IEnumerable<string> hashes) => RecoveryCodeHashes = [.. hashes];

    /// <summary>Spends a recovery code (each works once). False when it is not one of the unused codes.</summary>
    public bool UseRecoveryCode(string hash)
    {
        if (!RecoveryCodeHashes.Contains(hash))
        {
            return false;
        }

        RecoveryCodeHashes = [.. RecoveryCodeHashes.Where(h => h != hash)];
        return true;
    }

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void Disable() => Status = StaffStatus.Disabled;

    public void Activate()
    {
        Status = StaffStatus.Active;
        FailedLogins = 0;
        LockedUntil = null;
    }

    /// <summary>Clears TOTP so the next sign-in enrolls a new authenticator.</summary>
    public void ResetTotp()
    {
        TotpEnabled = false;
        TotpSecretEnc = null;
        LastTotpStep = null;
        RecoveryCodeHashes = [];
    }

    public void SetRoles(IEnumerable<string> roles) => Roles = [.. roles.Distinct(StringComparer.Ordinal)];
}

/// <summary>Staff refresh token (rotated like user sessions) with a 15-minute idle timeout and an 8-hour absolute limit.</summary>
public sealed class StaffSession : AggregateRoot<Guid>
{
    private StaffSession()
    {
    }

    public Guid StaffId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string RefreshHash { get; private set; } = string.Empty;

    public DateTimeOffset IdleExpiresAt { get; private set; }

    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? Ip { get; private set; }

    public string? UserAgent { get; private set; }

    public static StaffSession Start(Guid staffId, string refreshHash, TimeSpan idle, TimeSpan absolute, string? ip, string? ua, DateTimeOffset now)
    {
        var id = Uuid.NewV7(now);
        return new StaffSession
        {
            Id = id,
            StaffId = staffId,
            FamilyId = id,
            RefreshHash = refreshHash,
            IdleExpiresAt = now + idle,
            AbsoluteExpiresAt = now + absolute,
            Ip = ip,
            UserAgent = ua,
            CreatedAt = now,
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && IdleExpiresAt > now && AbsoluteExpiresAt > now;

    public StaffSession Rotate(string newHash, TimeSpan idle, string? ip, string? ua, DateTimeOffset now)
    {
        var next = new StaffSession
        {
            Id = Uuid.NewV7(now),
            StaffId = StaffId,
            FamilyId = FamilyId,
            RefreshHash = newHash,
            IdleExpiresAt = now + idle < AbsoluteExpiresAt ? now + idle : AbsoluteExpiresAt,
            AbsoluteExpiresAt = AbsoluteExpiresAt,
            Ip = ip,
            UserAgent = ua,
            CreatedAt = now,
        };
        Revoke("rotated", now);
        return next;
    }

    public void Revoke(string reason, DateTimeOffset now)
    {
        if (RevokedAt is null)
        {
            RevokedAt = now;
            RevokedReason = reason;
        }
    }
}

/// <summary>A global feature flag (AD-1.8). Evaluated by <see cref="Features.FeatureFlagEvaluator"/>.</summary>
public sealed class FeatureFlag
{
    public string Key { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool Enabled { get; private set; }

    /// <summary>0 to 100: the share of workspaces (stable per flag and workspace) that get the flag.</summary>
    public int RolloutPercent { get; private set; }

    /// <summary>Plan codes the flag is limited to; empty for every plan.</summary>
    public List<string> Plans { get; private set; } = [];

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public static FeatureFlag Create(string key, string? description, bool enabled, int rolloutPercent, List<string> plans, Guid by, DateTimeOffset now) => new()
    {
        Key = key,
        Description = description,
        Enabled = enabled,
        RolloutPercent = rolloutPercent,
        Plans = plans,
        CreatedAt = now,
        UpdatedAt = now,
        UpdatedBy = by,
    };

    public void Change(string? description, bool enabled, int rolloutPercent, List<string> plans, Guid by, DateTimeOffset now)
    {
        Description = description;
        Enabled = enabled;
        RolloutPercent = rolloutPercent;
        Plans = plans;
        UpdatedAt = now;
        UpdatedBy = by;
    }
}

/// <summary>A workspace's override of a flag: on or off whatever the rollout says.</summary>
public sealed class FeatureFlagOverride
{
    public required string Key { get; init; }

    public Guid WorkspaceId { get; init; }

    public bool Enabled { get; set; }

    public string Reason { get; set; } = string.Empty;

    public Guid UpdatedBy { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
