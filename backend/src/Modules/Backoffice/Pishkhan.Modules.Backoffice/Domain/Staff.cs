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

    public void SetPasswordHash(string hash) => PasswordHash = hash;

    public void Disable() => Status = StaffStatus.Disabled;
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
