using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Content.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Content.Services;

/// <summary>The public website's on-demand revalidation webhook (website <c>docs/backend-contract.md</c>).</summary>
public sealed class WebsiteOptions
{
    public const string Section = "Website";

    /// <summary>For example <c>https://pishkhan.ir/api/revalidate</c>. Empty: nothing is called (the site's own timers refresh it).</summary>
    public Uri? RevalidateUrl { get; set; }

    /// <summary>Shared HMAC secret, the site's <c>REVALIDATE_SECRET</c>. Required with <see cref="RevalidateUrl"/>.</summary>
    public string? RevalidateSecret { get; set; }
}

/// <summary>
/// Signs a webhook body the way the site checks it: <c>X-Pishkhan-Timestamp: unix seconds</c> and
/// <c>X-Pishkhan-Signature: sha256=hex(HMAC-SHA256(secret, "timestamp.body"))</c>. The site rejects timestamps more than 5 minutes off.
/// </summary>
public static class SiteSignature
{
    public const string TimestampHeader = "X-Pishkhan-Timestamp";
    public const string SignatureHeader = "X-Pishkhan-Signature";

    public static string Sign(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp.ToString(CultureInfo.InvariantCulture) + "." + body));
        return "sha256=" + Convert.ToHexStringLower(mac);
    }
}

/// <summary>
/// Platform group: calls the website's revalidation webhook. A failure throws, so the consumer retries and then parks the event in the
/// dead-letter topic; revalidating twice is harmless on the site.
/// </summary>
internal sealed class SiteRevalidationHandler(
    IHttpClientFactory http, IOptions<WebsiteOptions> options, IClock clock, ILogger<SiteRevalidationHandler> logger) : IIntegrationEventHandler<SiteRevalidationRequested>
{
    public const string ClientName = "website";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task HandleAsync(SiteRevalidationRequested message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (o.RevalidateUrl is null)
        {
            logger.LogDebug("Website:RevalidateUrl is not set; skipping revalidation of {Tags} {Paths}", message.Tags, message.Paths);
            return;
        }

        if (string.IsNullOrEmpty(o.RevalidateSecret))
        {
            throw new InvalidOperationException("Website:RevalidateSecret is not configured.");
        }

        var body = JsonSerializer.Serialize(new { tags = message.Tags, paths = message.Paths }, Json);
        var timestamp = clock.UtcNow.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, o.RevalidateUrl)
        {
            Content = new StringContent(body, Encoding.UTF8, new MediaTypeHeaderValue("application/json")),
        };
        request.Headers.Add(SiteSignature.TimestampHeader, timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(SiteSignature.SignatureHeader, SiteSignature.Sign(o.RevalidateSecret, timestamp, body));

        using var response = await http.CreateClient(ClientName).SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"The website's revalidation webhook answered {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }
}
