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

public sealed record SmsMessage(PhoneNumber To, string Text, string? Template = null, IReadOnlyList<string>? TemplateTokens = null);

/// <summary>One Iranian SMS provider behind this interface (assumption A7). OTPs use the provider's template ("lookup") API.</summary>
public interface ISmsSender
{
    Task SendAsync(SmsMessage message, CancellationToken cancellationToken = default);
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
public sealed class DevSmsSender([FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis, RedisKeys keys, ILogger<DevSmsSender> logger) : ISmsSender
{
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
public sealed class KavenegarSmsSender(HttpClient http, IOptions<SmsOptions> options) : ISmsSender
{
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
            services.AddHttpClient<ISmsSender, KavenegarSmsSender>(c => c.Timeout = TimeSpan.FromSeconds(15)).AddStandardResilienceHandler();
        }
        else
        {
            services.AddSingleton<DevSmsSender>();
            services.AddSingleton<ISmsSender>(sp => sp.GetRequiredService<DevSmsSender>());
        }

        return services;
    }
}
