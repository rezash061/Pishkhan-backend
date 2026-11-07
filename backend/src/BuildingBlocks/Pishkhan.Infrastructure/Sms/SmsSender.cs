using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.SharedKernel;
using StackExchange.Redis;

namespace Pishkhan.Infrastructure.Sms;

/// <summary>
/// One text. <see cref="Purpose"/> (otp, invitation, notification, campaign, test) and <see cref="WorkspaceId"/> go to the SMS log the back
/// office reads; the text itself is never logged.
/// </summary>
public sealed record SmsMessage(
    PhoneNumber To, string Text, string? Template = null, IReadOnlyList<string>? TemplateTokens = null, string Purpose = SmsPurposes.Other, Guid? WorkspaceId = null);

public static class SmsPurposes
{
    public const string Otp = "otp";
    public const string Invitation = "invitation";
    public const string Notification = "notification";
    public const string Campaign = "campaign";
    public const string Test = "test";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [Otp, Invitation, Notification, Campaign, Test, Other];
}

/// <summary>One Iranian SMS provider behind this interface (assumption A7). OTPs use the provider's template ("lookup") API.</summary>
public interface ISmsSender
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
}

/// <summary>The provider's state for the back office. <see cref="Credit"/> is the account's remaining credit in Rial when the provider says.</summary>
public sealed record SmsProviderStatus(string Provider, bool Configured, string? OtpTemplate, long? Credit, string? Error);

public interface ISmsProviderInfo
{
    Task<SmsProviderStatus> StatusAsync(CancellationToken cancellationToken);
}

public sealed class SmsOptions
{
    public const string Section = "Sms";

    /// <summary><c>dev</c> (log and keep the last messages in Redis) or <c>kavenegar</c>.</summary>
    public string Provider { get; set; } = "dev";

    public string? ApiKey { get; set; }

    public string BaseUrl { get; set; } = "https://api.kavenegar.com";

    public string OtpTemplate { get; set; } = "pishkhan-otp";
}

/// <summary>
/// Development sender: logs that a message was sent (never its text, which may hold an OTP) and keeps the last 50 messages in
/// the state Redis so the dev-only <c>GET /dev/sms</c> endpoint can show them, like Mailpit does for email.
/// </summary>
public sealed class DevSmsSender([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, ILogger<DevSmsSender> logger) : ISmsSender, ISmsProviderInfo
{
    public Task<SmsProviderStatus> StatusAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new SmsProviderStatus("dev", true, null, null, null));

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Dev SMS to {Phone} queued in the dev inbox", message.To.Masked);
        var entry = JsonSerializer.Serialize(new { to = message.To.E164, text = message.Text, at = DateTimeOffset.UtcNow });
        var db = redis.GetDatabase();
        await db.ListLeftPushAsync(keys.For("dev", "sms"), entry);
        await db.ListTrimAsync(keys.For("dev", "sms"), 0, 49);
    }

    public async Task<IReadOnlyList<string>> RecentAsync()
    {
        var values = await redis.GetDatabase().ListRangeAsync(keys.For("dev", "sms"), 0, 49);
        return [.. values.Select(v => v.ToString())];
    }
}

/// <summary>Kavenegar REST API: template lookup for OTPs, plain send otherwise.</summary>
public sealed class KavenegarSmsSender(HttpClient http, IOptions<SmsOptions> options) : ISmsSender, ISmsProviderInfo
{
    /// <summary>Kavenegar's <c>account/info</c>: <c>entries.remaincredit</c> in Rial.</summary>
    public async Task<SmsProviderStatus> StatusAsync(CancellationToken cancellationToken)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey))
        {
            return new SmsProviderStatus("kavenegar", false, o.OtpTemplate, null, "sms.not_configured");
        }

        try
        {
            using var response = await http.GetAsync(new Uri($"{o.BaseUrl}/v1/{o.ApiKey}/account/info.json"), cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return new SmsProviderStatus("kavenegar", true, o.OtpTemplate, null, $"sms.provider_{(int)response.StatusCode}");
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            long? credit = body.TryGetProperty("entries", out var entries) && entries.TryGetProperty("remaincredit", out var c) && c.TryGetInt64(out var v) ? v : null;
            return new SmsProviderStatus("kavenegar", true, o.OtpTemplate, credit, null);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return new SmsProviderStatus("kavenegar", true, o.OtpTemplate, null, "sms.provider_unreachable");
        }
    }

    public async Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default)
    {
        var o = options.Value;
        var receptor = Uri.EscapeDataString(message.To.National);
        string url;
        if (message.Template is not null && message.TemplateTokens is { Count: > 0 } tokens)
        {
            url = $"{o.BaseUrl}/v1/{o.ApiKey}/verify/lookup.json?receptor={receptor}&template={Uri.EscapeDataString(message.Template)}&token={Uri.EscapeDataString(tokens[0])}";
        }
        else
        {
            url = $"{o.BaseUrl}/v1/{o.ApiKey}/sms/send.json?receptor={receptor}&message={Uri.EscapeDataString(message.Text)}";
        }

        using var response = await http.PostAsync(new Uri(url), content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
            throw new HttpRequestException($"Kavenegar returned {(int)response.StatusCode}: {body.GetProperty("return").GetProperty("message").GetString()}");
        }
    }
}

public static class SmsExtensions
{
    public static IServiceCollection AddPishkhanSms(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<SmsOptions>().Bind(configuration.GetSection(SmsOptions.Section));
        if (string.Equals(configuration[$"{SmsOptions.Section}:Provider"], "kavenegar", StringComparison.OrdinalIgnoreCase))
        {
            services.AddHttpClient<KavenegarSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(15)).AddStandardResilienceHandler();
            services.AddTransient<ISmsProviderInfo>(sp => sp.GetRequiredService<KavenegarSmsSender>());
            services.AddTransient<ISmsSender>(sp => ActivatorUtilities.CreateInstance<LoggingSmsSender>(sp, sp.GetRequiredService<KavenegarSmsSender>(), "kavenegar"));
        }
        else
        {
            services.AddSingleton<DevSmsSender>();
            services.AddSingleton<ISmsProviderInfo>(sp => sp.GetRequiredService<DevSmsSender>());
            services.AddSingleton<ISmsSender>(sp => ActivatorUtilities.CreateInstance<LoggingSmsSender>(sp, sp.GetRequiredService<DevSmsSender>(), "dev"));
        }

        return services;
    }
}
