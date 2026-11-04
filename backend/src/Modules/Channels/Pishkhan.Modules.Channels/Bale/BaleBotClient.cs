using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using Pishkhan.Modules.Channels.Contracts;

namespace Pishkhan.Modules.Channels.Bale;

/// <summary>
/// Bale (بله), the first domestic messenger (assumption A7, BE-2.7). The seller connects their own Bale bot by its token; its updates come
/// to <c>/api/v1/channels/bale/webhook/{accountId}/{signature}</c>.
/// </summary>
public sealed class BaleOptions
{
    public const string Section = "Channels:Bale";

    /// <summary>The Bot API, Telegram-compatible: <c>{ApiBaseUrl}/bot{token}/{method}</c>.</summary>
    public string ApiBaseUrl { get; set; } = "https://tapi.bale.ai";

    /// <summary>The public base URL of the Api host, which Bale calls with updates.</summary>
    public string WebhookBaseUrl { get; set; } = "https://api.pishkhan.ir";

    /// <summary>Key of the webhook URL's signature (HMAC-SHA256 of the account id). Required in production.</summary>
    public string WebhookSecret { get; set; } = "dev-only-bale-webhook-secret";

    /// <summary>Deep links to a bot: <c>{DeepLinkBaseUrl}/{username}?start={token}</c>.</summary>
    public string DeepLinkBaseUrl { get; set; } = "https://ble.ir";
}

public sealed record BaleBot(long Id, string? Username, string? FirstName);

/// <summary>A Bot API error. <see cref="Retryable"/>: rate limited (429) or Bale failing (5xx, unreachable).</summary>
public sealed class BaleApiException(int status, string? description) : Exception($"Bale API error {status}: {description}")
{
    public int Status { get; } = status;

    public bool Retryable => Status is 0 or 429 or >= 500;

    public bool Unauthorized => Status is 401 or 403 or 404;

    public string ErrorCode => Status switch
    {
        0 => "bale.unreachable",
        401 or 404 => "bale.invalid_token",
        403 => "bale.blocked",
        429 => "bale.rate_limited",
        >= 500 => "bale.unavailable",
        _ => "bale.rejected",
    };
}

/// <summary>Deep links need no HTTP client: a singleton any host with the Channels module can use.</summary>
internal sealed class BaleLinks(IOptions<BaleOptions> options) : IBaleLinks
{
    public string DeepLink(string botUsername, string token) =>
        $"{options.Value.DeepLinkBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(botUsername)}?start={Uri.EscapeDataString(token)}";
}

public sealed class BaleBotClient(HttpClient http, IOptions<BaleOptions> options)
{
    private BaleOptions O => options.Value;

    public async Task<BaleBot> GetMeAsync(string token, CancellationToken ct)
    {
        var result = await CallAsync(token, "getMe", null, ct);
        return new BaleBot(
            result.GetProperty("id").GetInt64(),
            result.TryGetProperty("username", out var u) ? u.GetString() : null,
            result.TryGetProperty("first_name", out var n) ? n.GetString() : null);
    }

    public Task SetWebhookAsync(string token, Uri url, CancellationToken ct) =>
        CallAsync(token, "setWebhook", new JsonObject { ["url"] = url.ToString() }, ct);

    public Task DeleteWebhookAsync(string token, CancellationToken ct) => CallAsync(token, "deleteWebhook", null, ct);

    /// <summary>Sends a message; returns Bale's message id.</summary>
    public async Task<string> SendAsync(string token, string chatId, OutboundContent content, CancellationToken ct)
    {
        var markup = Keyboard(content);
        JsonElement result;
        if (content.Attachment is { Type: AttachmentTypes.Image, Url: { } photo })
        {
            var body = new JsonObject { ["chat_id"] = chatId, ["photo"] = photo.ToString() };
            if (!string.IsNullOrWhiteSpace(content.Text))
            {
                body["caption"] = content.Text;
            }

            if (markup is not null)
            {
                body["reply_markup"] = markup;
            }

            result = await CallAsync(token, "sendPhoto", body, ct);
        }
        else
        {
            var body = new JsonObject { ["chat_id"] = chatId, ["text"] = Text(content) };
            if (markup is not null)
            {
                body["reply_markup"] = markup;
            }

            result = await CallAsync(token, "sendMessage", body, ct);
        }

        return result.TryGetProperty("message_id", out var id) ? id.ToString() : string.Empty;
    }

    /// <summary>The webhook URL of an account: the account id and its HMAC signature, so a forged URL is refused without a lookup.</summary>
    public Uri WebhookUrl(Guid accountId) => new($"{O.WebhookBaseUrl.TrimEnd('/')}/api/v1/channels/bale/webhook/{accountId:N}/{Signature(accountId, O.WebhookSecret)}");

    public string DeepLink(string username, string token) => $"{O.DeepLinkBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(username)}?start={Uri.EscapeDataString(token)}";

    public bool IsValidSignature(Guid accountId, string signature) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Signature(accountId, O.WebhookSecret)), Encoding.ASCII.GetBytes(signature ?? string.Empty));

    public static string Signature(Guid accountId, string secret) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(accountId.ToString("N"))))[..40];

    /// <summary>Cards have no Bale equivalent: each becomes a line of the text, its buttons buttons.</summary>
    internal static string Text(OutboundContent content)
    {
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(content.Text))
        {
            lines.Add(content.Text);
        }

        foreach (var card in content.Cards ?? [])
        {
            lines.Add(card.Subtitle is { Length: > 0 } s ? $"• {card.Title}\n{s}" : $"• {card.Title}");
        }

        return lines.Count == 0 ? " " : string.Join("\n\n", lines);
    }

    /// <summary>Quick replies and postback buttons become callback buttons (their payload comes back as a postback); links become URL buttons.</summary>
    internal static JsonObject? Keyboard(OutboundContent content)
    {
        var rows = new JsonArray();
        foreach (var q in content.QuickReplies ?? [])
        {
            rows.Add(new JsonArray(new JsonObject { ["text"] = q.Title, ["callback_data"] = Clip(q.Payload) }));
        }

        foreach (var b in (content.Buttons ?? []).Concat((content.Cards ?? []).SelectMany(c => c.Buttons ?? [])))
        {
            rows.Add(new JsonArray(b.Url is { } url
                ? new JsonObject { ["text"] = b.Title, ["url"] = url.ToString() }
                : new JsonObject { ["text"] = b.Title, ["callback_data"] = Clip(b.Payload ?? b.Title) }));
        }

        return rows.Count == 0 ? null : new JsonObject { ["inline_keyboard"] = rows };
    }

    // Telegram-compatible callback data is at most 64 bytes.
    private static string Clip(string payload) => Encoding.UTF8.GetByteCount(payload) <= 64 ? payload : payload[..Math.Min(payload.Length, 20)];

    private async Task<JsonElement> CallAsync(string token, string method, JsonObject? body, CancellationToken ct)
    {
        var url = $"{O.ApiBaseUrl.TrimEnd('/')}/bot{token}/{method}";
        HttpResponseMessage response;
        try
        {
            response = body is null ? await http.PostAsync(url, null, ct) : await http.PostAsJsonAsync(url, body, ct);
        }
        catch (HttpRequestException e)
        {
            throw new BaleApiException(0, e.Message);
        }

        using (response)
        {
            JsonElement root;
            try
            {
                root = (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).Clone();
            }
            catch (JsonException)
            {
                throw new BaleApiException((int)response.StatusCode is 200 ? 502 : (int)response.StatusCode, "not json");
            }

            if (response.StatusCode == HttpStatusCode.OK && root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True)
            {
                return root.TryGetProperty("result", out var result) ? result.Clone() : default;
            }

            var code = root.TryGetProperty("error_code", out var c) && c.TryGetInt32(out var n) ? n : (int)response.StatusCode;
            throw new BaleApiException(code, root.TryGetProperty("description", out var d) ? d.GetString() : null);
        }
    }
}
