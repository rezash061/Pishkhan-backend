using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Pishkhan.Modules.Channels.Instagram;

/// <summary>
/// Thin client for the Instagram API with Instagram Login. Tokens are passed in per call and never logged
/// (the PII redaction enricher also strips <c>access_token</c> from logged URLs).
/// </summary>
public sealed class InstagramGraphClient(HttpClient http, IOptions<InstagramOptions> options)
{
    private InstagramOptions O => options.Value;

    public Uri BuildAuthorizeUrl(string state) => new(
        $"{O.AuthorizeUrl}?client_id={Uri.EscapeDataString(O.AppId)}&redirect_uri={Uri.EscapeDataString(O.RedirectUri)}"
        + $"&response_type=code&scope={Uri.EscapeDataString(string.Join(',', O.Scopes))}&state={Uri.EscapeDataString(state)}&enable_fb_login=0&force_authentication=1");

    /// <summary>Exchanges the OAuth code for a short-lived token (1 hour).</summary>
    public async Task<ShortLivedToken> ExchangeCodeAsync(string code, CancellationToken ct)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["client_id"] = O.AppId,
            ["client_secret"] = O.AppSecret,
            ["grant_type"] = "authorization_code",
            ["redirect_uri"] = O.RedirectUri,
            ["code"] = code,
        });
        using var response = await http.PostAsync(new Uri(O.TokenUrl), content, ct);
        var body = await ReadAsync(response, ct);

        // The endpoint has returned both a flat object and { data: [ {...} ] } over time.
        var token = body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : body;
        var permissions = token.TryGetProperty("permissions", out var p)
            ? p.ValueKind == JsonValueKind.Array
                ? p.EnumerateArray().Select(x => x.GetString()!).ToArray()
                : (p.GetString() ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [];
        return new ShortLivedToken(token.GetProperty("access_token").GetString()!, ReadId(token, "user_id"), permissions);
    }

    /// <summary>Exchanges a short-lived token for a 60-day token.</summary>
    public Task<LongLivedToken> ExchangeForLongLivedAsync(string shortLivedToken, CancellationToken ct) =>
        GetTokenAsync($"{O.GraphBaseUrl}/access_token?grant_type=ig_exchange_token&client_secret={Uri.EscapeDataString(O.AppSecret)}&access_token={Uri.EscapeDataString(shortLivedToken)}", ct);

    /// <summary>Refreshes a long-lived token that is at least 24 hours old and not expired.</summary>
    public Task<LongLivedToken> RefreshAsync(string longLivedToken, CancellationToken ct) =>
        GetTokenAsync($"{O.GraphBaseUrl}/refresh_access_token?grant_type=ig_refresh_token&access_token={Uri.EscapeDataString(longLivedToken)}", ct);

    public async Task<InstagramProfile> GetMeAsync(string token, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, $"{O.GraphBaseUrl}/{O.ApiVersion}/me?fields=user_id,username,name,account_type,profile_picture_url", token);
        using var response = await http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        return new InstagramProfile(
            ReadId(body, "user_id"),
            body.TryGetProperty("username", out var u) ? u.GetString() : null,
            body.TryGetProperty("name", out var n) ? n.GetString() : null,
            body.TryGetProperty("account_type", out var a) ? a.GetString() : null);
    }

    /// <summary>Subscribes the account to this app's webhook fields. Without it no DMs arrive.</summary>
    public async Task SubscribeAppAsync(string token, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"{O.GraphBaseUrl}/{O.ApiVersion}/me/subscribed_apps?subscribed_fields={string.Join(',', O.WebhookFields)}", token);
        using var response = await http.SendAsync(request, ct);
        await ReadAsync(response, ct);
    }

    public async Task UnsubscribeAppAsync(string token, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Delete, $"{O.GraphBaseUrl}/{O.ApiVersion}/me/subscribed_apps", token);
        using var response = await http.SendAsync(request, ct);
        await ReadAsync(response, ct);
    }

    /// <summary>Sends a DM or a private reply (body from <see cref="InstagramMessageBuilder"/>). Returns Meta's message id.</summary>
    public async Task<string?> SendAsync(string token, JsonObject message, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"{O.GraphBaseUrl}/{O.ApiVersion}/me/messages", token);
        request.Content = new StringContent(message.ToJsonString(InstagramMessageBuilder.WireJson), System.Text.Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        return body.TryGetProperty("message_id", out var id) ? id.GetString() : null;
    }

    /// <summary>Posts a public reply under a comment. Returns the new comment's id.</summary>
    public async Task<string?> ReplyToCommentAsync(string token, string commentId, string text, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, $"{O.GraphBaseUrl}/{O.ApiVersion}/{Uri.EscapeDataString(commentId)}/replies", token);
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>(StringComparer.Ordinal) { ["message"] = text });
        using var response = await http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        return body.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    /// <summary>Profile of a user who messaged the account (Instagram exposes it only after they started a conversation).</summary>
    public async Task<InstagramUserProfile> GetUserProfileAsync(string token, string userId, CancellationToken ct)
    {
        using var request = Authorized(
            HttpMethod.Get,
            $"{O.GraphBaseUrl}/{O.ApiVersion}/{Uri.EscapeDataString(userId)}?fields=name,username,profile_pic,follower_count,is_user_follow_business,is_business_follow_user",
            token);
        using var response = await http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        return new InstagramUserProfile(
            userId,
            Str(body, "name"),
            Str(body, "username"),
            Str(body, "profile_pic"),
            body.TryGetProperty("follower_count", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : null,
            Bool(body, "is_user_follow_business"),
            Bool(body, "is_business_follow_user"));
    }

    /// <summary>One page of the account's media, newest first.</summary>
    public async Task<InstagramMediaPage> ListMediaAsync(string token, string? after, int limit, CancellationToken ct)
    {
        var url = $"{O.GraphBaseUrl}/{O.ApiVersion}/me/media?fields=id,caption,media_type,media_url,thumbnail_url,permalink,timestamp&limit={Math.Clamp(limit, 1, 50)}"
            + (string.IsNullOrEmpty(after) ? string.Empty : $"&after={Uri.EscapeDataString(after)}");
        using var request = Authorized(HttpMethod.Get, url, token);
        using var response = await http.SendAsync(request, ct);
        var body = await ReadAsync(response, ct);
        var items = body.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray().Select(m => new InstagramMedia(
                ReadId(m, "id"),
                Str(m, "caption"),
                Str(m, "media_type") ?? "UNKNOWN",
                Str(m, "media_url"),
                Str(m, "thumbnail_url"),
                Str(m, "permalink"),
                Str(m, "timestamp") is { } ts && DateTimeOffset.TryParse(ts, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null)).ToList()
            : [];

        // A next cursor without a "next" link means this was the last page.
        var next = body.TryGetProperty("paging", out var paging) && paging.TryGetProperty("next", out _)
            && paging.TryGetProperty("cursors", out var cursors) && cursors.TryGetProperty("after", out var a) ? a.GetString() : null;
        return new InstagramMediaPage(items, next);
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool? Bool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private async Task<LongLivedToken> GetTokenAsync(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(url), ct);
        var body = await ReadAsync(response, ct);
        return new LongLivedToken(body.GetProperty("access_token").GetString()!, body.TryGetProperty("expires_in", out var e) ? e.GetInt64() : 5_184_000);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string url, string token)
    {
        var request = new HttpRequestMessage(method, new Uri(url));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static string ReadId(JsonElement element, string name) =>
        element.GetProperty(name) is var v && v.ValueKind == JsonValueKind.Number ? v.GetInt64().ToString(CultureInfo.InvariantCulture) : v.GetString()!;

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        JsonElement body;
        try
        {
            body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        }
        catch (JsonException) when (!response.IsSuccessStatusCode)
        {
            throw new InstagramApiException(response.StatusCode, null, null, "non-JSON error response");
        }

        if (!response.IsSuccessStatusCode || body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out _))
        {
            throw InstagramApiException.From(response.StatusCode, body);
        }

        return body;
    }

}

public sealed record ShortLivedToken(string AccessToken, string UserId, IReadOnlyList<string> Permissions);

public sealed record LongLivedToken(string AccessToken, long ExpiresInSeconds);

public sealed record InstagramProfile(string UserId, string? Username, string? Name, string? AccountType);

public sealed record InstagramUserProfile(string UserId, string? Name, string? Username, string? ProfilePicture, int? FollowerCount, bool? FollowsBusiness, bool? BusinessFollowsUser);

public sealed record InstagramMedia(string Id, string? Caption, string MediaType, string? MediaUrl, string? ThumbnailUrl, string? Permalink, DateTimeOffset? Timestamp);

public sealed record InstagramMediaPage(IReadOnlyList<InstagramMedia> Items, string? NextCursor);

/// <summary>A Graph API error. <see cref="Kind"/> drives retry and status decisions.</summary>
public sealed class InstagramApiException(HttpStatusCode status, int? code, int? subcode, string? message)
    : Exception($"Instagram API error {(int)status} code={code} subcode={subcode}: {message}")
{
    public HttpStatusCode Status { get; } = status;

    public int? Code { get; } = code;

    public int? Subcode { get; } = subcode;

    public InstagramErrorKind Kind => Classify(Status, Code, Subcode);

    public string ErrorCode => Kind switch
    {
        InstagramErrorKind.InvalidToken => "instagram.token_invalid",
        InstagramErrorKind.RateLimited => "instagram.rate_limited",
        InstagramErrorKind.OutsideWindow => "instagram.outside_messaging_window",
        InstagramErrorKind.RecipientUnavailable => "instagram.recipient_unavailable",
        InstagramErrorKind.Transient => "instagram.transient",
        _ => "instagram.rejected",
    };

    public bool Retryable => Kind is InstagramErrorKind.RateLimited or InstagramErrorKind.Transient;

    public static InstagramApiException From(HttpStatusCode status, JsonElement body)
    {
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.Object)
        {
            return new InstagramApiException(
                status,
                e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : null,
                e.TryGetProperty("error_subcode", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : null,
                e.TryGetProperty("message", out var m) ? m.GetString() : null);
        }

        return new InstagramApiException(status, null, null, null);
    }

    /// <summary>Meta error codes: 190 token, 4/17/32/613 throttling, 10 + 2534022 outside the 24h window, 551 user unavailable, 1/2 and 5xx transient.</summary>
    public static InstagramErrorKind Classify(HttpStatusCode status, int? code, int? subcode) => (code, subcode) switch
    {
        (190, _) or (102, _) => InstagramErrorKind.InvalidToken,
        (4, _) or (17, _) or (32, _) or (613, _) => InstagramErrorKind.RateLimited,
        (_, 2534022) or (10, _) => InstagramErrorKind.OutsideWindow,
        (551, _) or (100, 2534014) => InstagramErrorKind.RecipientUnavailable,
        (1, _) or (2, _) => InstagramErrorKind.Transient,
        _ when (int)status >= 500 || status == HttpStatusCode.TooManyRequests => status == HttpStatusCode.TooManyRequests ? InstagramErrorKind.RateLimited : InstagramErrorKind.Transient,
        _ when status == HttpStatusCode.Unauthorized => InstagramErrorKind.InvalidToken,
        _ => InstagramErrorKind.Rejected,
    };
}

public enum InstagramErrorKind
{
    Rejected,
    InvalidToken,
    RateLimited,
    OutsideWindow,
    RecipientUnavailable,
    Transient,
}
