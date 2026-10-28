namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>Meta app settings for Instagram API with Instagram Login (business login). Secrets come from the environment or Vault.</summary>
public sealed class InstagramOptions
{
    public const string Section = "Instagram";

    public string AppId { get; set; } = string.Empty;

    public string AppSecret { get; set; } = string.Empty;

    /// <summary>Token Meta echoes on the webhook verification GET.</summary>
    public string WebhookVerifyToken { get; set; } = string.Empty;

    /// <summary>Must exactly match a redirect URI registered in the Meta app, for example <c>https://api.pishkhan.ir/api/v1/channels/instagram/callback</c>.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    /// <summary>Panel page the OAuth callback returns the browser to.</summary>
    public string PanelReturnUrl { get; set; } = "http://localhost:3001/channels";

    /// <summary>Base for the public data-deletion status page shown to Meta users.</summary>
    public string DataDeletionStatusUrl { get; set; } = "https://pishkhan.ir/data-deletion";

    public string AuthorizeUrl { get; set; } = "https://www.instagram.com/oauth/authorize";

    public string TokenUrl { get; set; } = "https://api.instagram.com/oauth/access_token";

    public string GraphBaseUrl { get; set; } = "https://graph.instagram.com";

    public string ApiVersion { get; set; } = "v24.0";

    public string[] Scopes { get; set; } =
        ["instagram_business_basic", "instagram_business_manage_messages", "instagram_business_manage_comments"];

    public string[] WebhookFields { get; set; } = ["messages", "messaging_postbacks", "messaging_seen", "comments"];

    /// <summary>Rate profiles by name: outbound send token bucket per account. Admin-editable later (Meta limits change).</summary>
    public Dictionary<string, RateProfile> RateProfiles { get; set; } = new(StringComparer.Ordinal)
    {
        ["default"] = new RateProfile { Capacity = 20, RefillPerSecond = 5 },
    };

    public RateProfile ProfileFor(string name) =>
        RateProfiles.TryGetValue(name, out var profile) ? profile : RateProfiles.GetValueOrDefault("default") ?? new RateProfile();
}

public sealed class RateProfile
{
    public int Capacity { get; set; } = 20;

    public double RefillPerSecond { get; set; } = 5;
}
