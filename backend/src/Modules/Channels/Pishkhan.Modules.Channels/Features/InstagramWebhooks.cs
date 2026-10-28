using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Instagram;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;
using StackExchange.Redis;

namespace Pishkhan.Modules.Channels.Features;

/// <summary>
/// Webhook ingress (Ingress host, international zone). It does no database work: verify the signature on the raw bytes,
/// drop duplicates, produce one <see cref="InstagramWebhookReceived"/> per entry and answer 200 fast (backend plan 9.3).
/// </summary>
internal static class InstagramWebhooks
{
    public const int MaxBodyBytes = 1024 * 1024;
    private static readonly TimeSpan DedupeWindow = TimeSpan.FromHours(24);
    private static readonly Meter Meter = new("Pishkhan.Channels");
    private static readonly Counter<long> Received = Meter.CreateCounter<long>("pishkhan.webhooks.received", description: "Webhook requests by result");

    public static void MapIngress(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/webhooks/instagram").AllowAnonymous().ExcludeFromDescription();
        group.MapGet("/", Verify);
        group.MapPost("/", ReceiveAsync).DisableAntiforgery();
    }

    private static IResult Verify(HttpContext http, IOptions<InstagramOptions> options)
    {
        var q = http.Request.Query;
        var expected = options.Value.WebhookVerifyToken;
        return q["hub.mode"] == "subscribe"
            && !string.IsNullOrEmpty(expected)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(q["hub.verify_token"].ToString()), Encoding.UTF8.GetBytes(expected))
                ? Results.Text(q["hub.challenge"].ToString(), "text/plain")
                : Results.StatusCode(StatusCodes.Status403Forbidden);
    }

    private static async Task<IResult> ReceiveAsync(
        HttpContext http,
        IOptions<InstagramOptions> options,
        IKafkaProducer producer,
        [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
        RedisKeys keys,
        IClock clock,
        ILoggerFactory loggers)
    {
        var logger = loggers.CreateLogger(typeof(InstagramWebhooks));
        var ct = http.RequestAborted;
        if (http.Request.ContentLength > MaxBodyBytes)
        {
            Received.Add(1, new KeyValuePair<string, object?>("result", "too_large"));
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
        {
            limit.MaxRequestBodySize = MaxBodyBytes;
        }

        byte[] body;
        try
        {
            body = await ReadBodyAsync(http.Request.Body, ct);
        }
        catch (Exception ex) when (ex is InvalidDataException || ex is BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge })
        {
            Received.Add(1, new KeyValuePair<string, object?>("result", "too_large"));
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Signature first: nothing of an unsigned body is parsed.
        if (!InstagramSignatures.VerifyWebhook(body, http.Request.Headers["X-Hub-Signature-256"], options.Value.AppSecret))
        {
            Received.Add(1, new KeyValuePair<string, object?>("result", "bad_signature"));
            logger.LogWarning("Instagram webhook with an invalid signature from {RemoteIp}", http.Connection.RemoteIpAddress);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, type: ProblemTypes.Unauthorized, title: "webhook.invalid_signature");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            Received.Add(1, new KeyValuePair<string, object?>("result", "bad_json"));
            return Results.BadRequest();
        }

        using (document)
        {
            var root = document.RootElement;
            var obj = root.TryGetProperty("object", out var o) ? o.GetString() ?? string.Empty : string.Empty;
            if (!root.TryGetProperty("entry", out var entries) || entries.ValueKind != JsonValueKind.Array)
            {
                Received.Add(1, new KeyValuePair<string, object?>("result", "ignored"));
                return Results.Ok();
            }

            var db = redis.GetDatabase();
            var now = clock.UtcNow;
            foreach (var entry in entries.EnumerateArray())
            {
                var accountId = entry.TryGetProperty("id", out var id) ? id.ValueKind == JsonValueKind.Number ? id.GetRawText() : id.GetString() : null;
                if (string.IsNullOrEmpty(accountId))
                {
                    continue;
                }

                var dedupeKey = keys.For("igwh", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(entry.GetRawText()))));
                if (!await db.StringSetAsync(dedupeKey, 1, DedupeWindow, When.NotExists))
                {
                    Received.Add(1, new KeyValuePair<string, object?>("result", "duplicate"));
                    continue;
                }

                try
                {
                    await producer.PublishAsync(new InstagramWebhookReceived(obj, accountId, entry.Clone(), now), accountId, cancellationToken: ct);
                }
                catch (Exception ex) when (ex is Confluent.Kafka.KafkaException or OperationCanceledException)
                {
                    // Let Meta retry: forget the dedupe mark and answer 5xx.
                    await db.KeyDeleteAsync(dedupeKey);
                    logger.LogError(ex, "Could not produce Instagram webhook for account {AccountExternalId}", accountId);
                    Received.Add(1, new KeyValuePair<string, object?>("result", "produce_failed"));
                    return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
                }

                Received.Add(1, new KeyValuePair<string, object?>("result", "accepted"));
            }
        }

        return Results.Ok();
    }

    private static async Task<byte[]> ReadBodyAsync(Stream body, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes)
            {
                throw new InvalidDataException("Body too large.");
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }
}

public sealed record DataDeletionResponse(Uri Url, string ConfirmationCode);

public sealed record DataDeletionStatusResponse(string ConfirmationCode, string Status, DateTimeOffset CreatedAt);

/// <summary>Meta's required signed callbacks (Api host, public): deauthorize and data deletion.</summary>
internal static class MetaCallbacks
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapPublicApi().MapGroup("/meta/instagram").WithTags("Meta callbacks");

        group.MapPost("/deauthorize", DeauthorizeAsync).DisableAntiforgery().ExcludeFromDescription();
        group.MapPost("/data-deletion", RequestDeletionAsync).DisableAntiforgery().ExcludeFromDescription();
        group.MapGet("/data-deletion/{code}", DeletionStatusAsync)
            .WithSummary("Status of a Meta data deletion request")
            .Produces<DataDeletionStatusResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);
    }

    private static async Task<IResult> DeauthorizeAsync(HttpContext http, ChannelsDbContext db, ITenantContext tenant, IOptions<InstagramOptions> options, ILoggerFactory loggers)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (InstagramSignatures.ParseSignedRequest(form["signed_request"], options.Value.AppSecret) is not { } payload
            || !TryUserId(payload, out var externalId))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, type: ProblemTypes.Unauthorized, title: "webhook.invalid_signature");
        }

        using var system = tenant.BeginSystemScope();
        var accounts = await db.Accounts.IgnoreQueryFilters([ModuleDbContext.TenantFilter])
            .Where(a => a.Kind == ChannelKinds.Instagram && a.ExternalId == externalId && a.Status != ChannelStatuses.Disconnected)
            .ToListAsync(http.RequestAborted);
        foreach (var account in accounts)
        {
            account.Disconnect();
            db.Publish(new ChannelStatusChanged(account.Id, account.WorkspaceId, account.Status, "deauthorized"), account.Id.ToString(), account.WorkspaceId);
        }

        await db.SaveChangesAsync(http.RequestAborted);
        loggers.CreateLogger(typeof(MetaCallbacks)).LogInformation("Instagram deauthorize for {ExternalId}: {Count} account(s) disconnected", externalId, accounts.Count);
        return Results.Ok();
    }

    private static async Task<IResult> RequestDeletionAsync(HttpContext http, ChannelsDbContext db, IOptions<InstagramOptions> options, IClock clock)
    {
        var form = await http.Request.ReadFormAsync(http.RequestAborted);
        if (InstagramSignatures.ParseSignedRequest(form["signed_request"], options.Value.AppSecret) is not { } payload
            || !TryUserId(payload, out var externalId))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, type: ProblemTypes.Unauthorized, title: "webhook.invalid_signature");
        }

        var now = clock.UtcNow;
        var request = new DataDeletionRequest
        {
            Id = Uuid.NewV7(now),
            ConfirmationCode = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10)),
            ExternalUserId = externalId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.DataDeletionRequests.Add(request);
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Json(
            new DataDeletionResponse(new Uri($"{options.Value.DataDeletionStatusUrl}?code={request.ConfirmationCode}"), request.ConfirmationCode),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower });
    }

    private static async Task<IResult> DeletionStatusAsync(string code, ChannelsDbContext db, CancellationToken ct)
    {
        var request = await db.DataDeletionRequests.AsNoTracking().FirstOrDefaultAsync(r => r.ConfirmationCode == code, ct);
        return request is null
            ? Error.NotFound().ToProblem()
            : Results.Ok(new DataDeletionStatusResponse(request.ConfirmationCode, request.Status, request.CreatedAt));
    }

    private static bool TryUserId(JsonElement payload, out string userId)
    {
        userId = payload.TryGetProperty("user_id", out var u) ? u.ValueKind == JsonValueKind.Number ? u.GetRawText() : u.GetString() ?? string.Empty : string.Empty;
        return userId.Length > 0;
    }
}
