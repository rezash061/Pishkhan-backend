namespace Pishkhan.Modules.Identity;

public sealed class IdentityOptions
{
    public const string Section = "Identity";

    public TimeSpan OtpLifetime { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan OtpResendCooldown { get; set; } = TimeSpan.FromSeconds(60);

    public int OtpMaxAttempts { get; set; } = 5;

    public int OtpPerPhonePerHour { get; set; } = 5;

    public int OtpPerIpPerHour { get; set; } = 20;

    public TimeSpan RefreshTokenLifetime { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan SessionFamilyLifetime { get; set; } = TimeSpan.FromDays(180);

    public TimeSpan RealtimeTokenLifetime { get; set; } = TimeSpan.FromMinutes(5);

    public int PasswordFailuresPerHour { get; set; } = 10;
}
