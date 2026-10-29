using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pishkhan.Infrastructure.Sms;

namespace Pishkhan.IntegrationTests;

/// <summary>The Api host in memory against the shared containers.</summary>
public sealed class ApiFactory(InfraFixture infra) : WebApplicationFactory<Program>
{
    private bool _created;

    protected override IHost CreateHost(IHostBuilder builder)
    {
        _created = true;
        return base.CreateHost(builder);
    }

    /// <summary>
    /// The bus starts in the background. Stopping a host whose bus is still starting leaves its Kafka consumers running on a disposed
    /// container: they keep their partitions in the shared groups and fault every message, so a short test class would starve the next one.
    /// </summary>
    public override async ValueTask DisposeAsync()
    {
        if (_created)
        {
            var bus = Services.GetRequiredService<IBusControl>();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (bus.CheckHealth().Status != BusHealthStatus.Healthy && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }
        }

        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting lands before Program reads configuration (minimal hosting applies ConfigureAppConfiguration only at Build).
        foreach (var (key, value) in infra.Settings)
        {
            builder.UseSetting(key, value);
        }
    }

    /// <summary>Signs a new user in with a phone OTP (dev SMS sender) and returns a client carrying the access token.</summary>
    public async Task<HttpClient> SignInAsync(CancellationToken ct)
    {
        var client = CreateClient();
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        (await client.PostAsJsonAsync("/api/v1/auth/otp/request", new { phone }, ct)).EnsureSuccessStatusCode();

        var sms = Services.GetRequiredService<DevSmsSender>();
        var code = (await sms.RecentAsync())
            .Select(item => JsonDocument.Parse(item).RootElement)
            .Where(e => e.GetProperty("to").GetString()!.EndsWith(phone[1..], StringComparison.Ordinal))
            .Select(e => Regex.Match(ToAscii(e.GetProperty("text").GetString()!), @"\d{6}").Value)
            .First();
        var verify = await client.PostAsJsonAsync("/api/v1/auth/otp/verify", new { phone, code }, ct);
        verify.EnsureSuccessStatusCode();
        var token = (await verify.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public static async Task<Guid> CreateWorkspaceAsync(HttpClient client, CancellationToken ct)
    {
        var response = await client.PostAsJsonAsync("/api/v1/workspaces", new { name = "فروشگاه آزمایشی", industry = "clothing" }, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("id").GetGuid();
    }

    private static string ToAscii(string text) =>
        string.Concat(text.Select(c => c is >= '۰' and <= '۹' ? (char)('0' + (c - '۰')) : c));
}
