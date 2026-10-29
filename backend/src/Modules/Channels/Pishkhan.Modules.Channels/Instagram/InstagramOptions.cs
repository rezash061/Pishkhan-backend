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

    /// <summary>Fields the account is subscribed to on connect (Instagram API with Instagram Login names).</summary>
    public string[] WebhookFields { get; set; } =
        ["messages", "messaging_postbacks", "messaging_seen", "message_reactions", "messaging_referral", "comments", "live_comments"];

    /// <summary>Long-lived tokens last 60 days; refresh them this long before they expire.</summary>
    public TimeSpan RefreshBefore { get; set; } = TimeSpan.FromDays(10);

    /// <summary>When refreshing keeps failing and expiry is this close, the account shows <c>token_expiring</c> to the owner.</summary>
    public TimeSpan ExpiringWarning { get; set; } = TimeSpan.FromDays(3);

    /// <summary>Default rate profiles by name. Admin overrides in <c>channels.rate_profiles</c> win (Meta limits change).</summary>
    public Dictionary<string, RateProfile> RateProfiles { get; set; } = new(StringComparer.Ordinal)
    {
        [RateProfile.Default] = new RateProfile(),
    };
}

/// <summary>Token buckets per Instagram API family for one account.</summary>
public sealed class RateProfile
{
    public const string Default = "default";

    public RateBucket Send { get; set; } = new() { Capacity = 20, RefillPerSecond = 5 };

    public RateBucket PrivateReply { get; set; } = new() { Capacity = 10, RefillPerSecond = 1 };

    public RateBucket CommentReply { get; set; } = new() { Capacity = 10, RefillPerSecond = 0.5 };

    public RateBucket Read { get; set; } = new() { Capacity = 30, RefillPerSecond = 2 };

    public RateBucket For(string family) => family switch
    {
        RateFamilies.Send => Send,
        RateFamilies.PrivateReply => PrivateReply,
        RateFamilies.CommentReply => CommentReply,
        RateFamilies.Read => Read,
        _ => throw new ArgumentOutOfRangeException(nameof(family), family, "Unknown rate family."),
    };
}

public sealed class RateBucket
{
    public int Capacity { get; set; } = 20;

    public double RefillPerSecond { get; set; } = 5;
}

public static class RateFamilies
{
    public const string Send = "send";
    public const string PrivateReply = "private_reply";
    public const string CommentReply = "comment_reply";
    public const string Read = "read";
}
