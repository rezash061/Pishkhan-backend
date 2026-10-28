using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Identity.Domain;

public enum UserStatus
{
    Active,
    Suspended,
}

/// <summary>A person who signs in to the panel. The phone number is stored encrypted with a blind index for lookup.</summary>
public sealed class User : AggregateRoot<Guid>
{
    private User()
    {
    }

    public required string PhoneE164Enc { get; set; }

    public required string PhoneBidx { get; set; }

    public string? DisplayName { get; private set; }

    public string? PasswordHash { get; private set; }

    public string? TotpSecretEnc { get; private set; }

    public bool TotpEnabled { get; private set; }

    public long? LastTotpStep { get; private set; }

    public UserStatus Status { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public static User Register(string phoneEnc, string phoneBidx, DateTimeOffset now) => new()
    {
        Id = Uuid.NewV7(now),
        PhoneE164Enc = phoneEnc,
        PhoneBidx = phoneBidx,
        Status = UserStatus.Active,
        CreatedAt = now,
    };

    public void Rename(string? displayName) => DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();

    public void SetPassword(string hash) => PasswordHash = hash;

    public void RecordLogin(DateTimeOffset now) => LastLoginAt = now;

    public void BeginTotpSetup(string secretEnc)
    {
        if (TotpEnabled)
        {
            throw new DomainException("auth.totp.already_enabled");
        }

        TotpSecretEnc = secretEnc;
    }

    public void EnableTotp(long step)
    {
        if (TotpSecretEnc is null)
        {
            throw new DomainException("auth.totp.not_set_up");
        }

        TotpEnabled = true;
        LastTotpStep = step;
    }

    public void DisableTotp()
    {
        TotpEnabled = false;
        TotpSecretEnc = null;
        LastTotpStep = null;
    }

    /// <summary>Rejects a TOTP code whose time step was already used (replay).</summary>
    public bool AcceptTotpStep(long step)
    {
        if (LastTotpStep is { } last && step <= last)
        {
            return false;
        }

        LastTotpStep = step;
        return true;
    }

    public void Suspend() => Status = UserStatus.Suspended;

    public void Reactivate() => Status = UserStatus.Active;
}

/// <summary>
/// One refresh token of a session family. Every refresh rotates: the used row is revoked with reason "rotated" and a new row
/// joins the same family. Presenting a rotated token again means it was stolen, so the whole family is revoked.
/// </summary>
public sealed class Session : AggregateRoot<Guid>
{
    private Session()
    {
    }

    public Guid UserId { get; private set; }

    /// <summary>The user-visible session id (the <c>sid</c> claim); stable across rotations.</summary>
    public Guid FamilyId { get; private set; }

    public required string RefreshHash { get; set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>The family cannot be refreshed after this time, however often it rotates.</summary>
    public DateTimeOffset FamilyExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public Guid? ReplacedBy { get; private set; }

    public string? Ip { get; private set; }

    public string? UserAgent { get; private set; }

    public string AuthMethods { get; private set; } = "otp";

    public const string ReasonRotated = "rotated";
    public const string ReasonReuse = "reuse_detected";
    public const string ReasonLogout = "logout";
    public const string ReasonRevokedByUser = "revoked_by_user";

    public static Session Start(Guid userId, string refreshHash, string amr, TimeSpan lifetime, TimeSpan familyLifetime, string? ip, string? ua, DateTimeOffset now)
    {
        var id = Uuid.NewV7(now);
        return new Session
        {
            Id = id,
            UserId = userId,
            FamilyId = id,
            RefreshHash = refreshHash,
            ExpiresAt = now + lifetime,
            FamilyExpiresAt = now + familyLifetime,
            Ip = ip,
            UserAgent = ua,
            AuthMethods = amr,
            CreatedAt = now,
        };
    }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now && FamilyExpiresAt > now;

    public Session Rotate(string newRefreshHash, TimeSpan lifetime, string? ip, string? ua, DateTimeOffset now)
    {
        var next = new Session
        {
            Id = Uuid.NewV7(now),
            UserId = UserId,
            FamilyId = FamilyId,
            RefreshHash = newRefreshHash,
            ExpiresAt = Min(now + lifetime, FamilyExpiresAt),
            FamilyExpiresAt = FamilyExpiresAt,
            Ip = ip,
            UserAgent = ua,
            AuthMethods = AuthMethods,
            CreatedAt = now,
        };
        Revoke(ReasonRotated, now);
        ReplacedBy = next.Id;
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

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
