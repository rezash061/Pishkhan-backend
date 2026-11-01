using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.14: handoffs and requests become in-app notifications per preferences; critical kinds send at most one SMS per cooldown.</summary>
[Collection(WorkersCollection.Name)]
public sealed class NotificationsTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<string> SeedAccountAsync(Guid ws)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1788" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        db.Accounts.Add(ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        return externalId;
    }

    private async Task PublishAsync<TEvent>(TEvent message, string key, Guid? ws)
        where TEvent : IIntegrationEvent
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Publish(message, key, ws);
        await db.SaveChangesAsync(Ct);
    }

    private async Task DmAsync(string account, string customer, string text) =>
        await PublishAsync(new InstagramWebhookReceived("instagram", account, JsonSerializer.SerializeToElement(new
        {
            id = account,
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            messaging = new[]
            {
                new
                {
                    sender = new { id = customer },
                    recipient = new { id = account },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text },
                },
            },
        }), DateTimeOffset.UtcNow), account, null);

    /// <summary>SMS to <paramref name="phone"/> containing <paramref name="marker"/>; other tests text other owners the same titles.</summary>
    private async Task<int> SmsCountAsync(string phone, string marker) =>
        (await workers.Host.Services.GetRequiredService<DevSmsSender>().RecentAsync())
            .Select(s => JsonDocument.Parse(s).RootElement)
            .Count(e => e.GetProperty("to").GetString()!.EndsWith(phone[1..], StringComparison.Ordinal)
                && e.GetProperty("text").GetString()!.Contains(marker, StringComparison.Ordinal));

    private static async Task<JsonElement> Eventually(Func<Task<JsonElement>> read, Func<JsonElement, bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var value = await read();
            if (condition(value))
            {
                return value;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Handoffs_and_requests_notify_per_preferences_and_critical_ones_send_one_sms()
    {
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        var owner = await _api.SignInAsync(phone, Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var account = await SeedAccountAsync(ws);
        var prefs = $"/api/v1/workspaces/{ws}/notification-preferences";

        // Defaults: in-app and push on, SMS only for channel problems; automations never offer SMS.
        var defaults = await owner.GetFromJsonAsync<JsonElement>(prefs, Ct);
        defaults.EnumerateArray().Select(p => (p.GetProperty("kind").GetString(), p.GetProperty("sms").GetBoolean(), p.GetProperty("smsAvailable").GetBoolean()))
            .ShouldBe([("handoff", false, true), ("automation", false, false), ("channel", true, true)]);
        (await owner.PutAsJsonAsync(prefs, new { items = new[] { new { kind = "automation", inApp = true, push = true, sms = true } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync(prefs, new { items = new[] { new { kind = "nope", inApp = true, push = true, sms = false } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var updated = await owner.PutAsJsonAsync(prefs, new { items = new[] { new { kind = "automation", inApp = false, push = false, sms = false } } }, Ct);
        updated.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await updated.Content.ReadFromJsonAsync<JsonElement>(Ct)).EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "automation").GetProperty("inApp").GetBoolean().ShouldBeFalse();

        // A handoff from a rule reaches the owner in-app.
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/automation/rules", new
        {
            name = "اپراتور",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "اپراتور" } },
            actions = new[] { new { kind = "handoff", reason = "customer_asked" } },
            priority = 0,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await DmAsync(account, "c_" + Guid.NewGuid().ToString("N")[..10], "اپراتور لطفا");
        var list = $"/api/v1/workspaces/{ws}/notifications";
        var page = await Eventually(() => owner.GetFromJsonAsync<JsonElement>(list, Ct), p => p.GetProperty("unreadCount").GetInt32() == 1, "the handoff notification");
        var handoff = page.GetProperty("items")[0];
        handoff.GetProperty("kind").GetString().ShouldBe("handoff");
        handoff.GetProperty("link").GetString()!.ShouldStartWith("/inbox/");

        // Automation notifications are off for the owner now; channel problems text them once per 30 minutes.
        var marker = Guid.NewGuid().ToString("N")[..8];
        await PublishAsync(new NotificationRequested(ws, null, "automation", "خاموش " + marker, null, null), ws.ToString(), ws);
        await PublishAsync(new NotificationRequested(ws, null, "channel", "اتصال " + marker, null, "/channels"), ws.ToString(), ws);
        await Eventually(async () => JsonSerializer.SerializeToElement(await SmsCountAsync(phone, "اتصال " + marker)), n => n.GetInt32() == 1, "the channel SMS");
        await PublishAsync(new ChannelStatusChanged(Guid.NewGuid(), ws, ChannelStatuses.ReauthRequired, "invalid_token"), ws.ToString(), ws);
        page = await Eventually(() => owner.GetFromJsonAsync<JsonElement>(list, Ct), p => p.GetProperty("unreadCount").GetInt32() == 3, "the channel notifications");
        page.GetProperty("items").EnumerateArray().ShouldNotContain(n => n.GetProperty("kind").GetString() == "automation");
        page.GetProperty("items")[0].GetProperty("title").GetString()!.ShouldContain("دوباره وصل کنید");
        await Task.Delay(500, Ct);
        (await SmsCountAsync(phone, "اتصال " + marker)).ShouldBe(1);
        (await SmsCountAsync(phone, "دوباره وصل کنید")).ShouldBe(0); // the second channel SMS falls in the cooldown

        // Paging, then read one and the rest.
        var first = await owner.GetFromJsonAsync<JsonElement>(list + "?limit=2", Ct);
        first.GetProperty("items").GetArrayLength().ShouldBe(2);
        var second = await owner.GetFromJsonAsync<JsonElement>(list + "?limit=2&cursor=" + first.GetProperty("nextCursor").GetString(), Ct);
        second.GetProperty("items").GetArrayLength().ShouldBe(1);
        second.GetProperty("nextCursor").ValueKind.ShouldBe(JsonValueKind.Null);

        var id = first.GetProperty("items")[0].GetProperty("id").GetGuid();
        (await owner.PostAsync($"{list}/{id}/read", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<JsonElement>(list + "?unread=true", Ct)).GetProperty("items").GetArrayLength().ShouldBe(2);
        (await owner.PostAsync($"{list}/read-all", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<JsonElement>(list, Ct)).GetProperty("unreadCount").GetInt32().ShouldBe(0);

        // Another member's notification is not reachable.
        var stranger = await _api.SignInAsync(Ct);
        (await stranger.GetAsync(list, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Web_push_reaches_subscribed_browsers_and_a_waiting_handoff_texts_after_the_delay()
    {
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        var owner = await _api.SignInAsync(phone, Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var account = await SeedAccountAsync(ws);

        // Subscribe a browser; endpoints outside the push services and malformed keys are refused.
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/me/push/key", Ct)).GetProperty("publicKey").GetString().ShouldBe(FakePushService.VapidPublicKey);
        using var browser = new FakePushService.Browser();
        var keys = new { p256dh = Pishkhan.Modules.Notifications.Services.Base64Url.Encode(browser.PublicKey), auth = Pishkhan.Modules.Notifications.Services.Base64Url.Encode(browser.AuthSecret) };
        var endpoint = $"https://fcm.googleapis.com/fcm/send/{Guid.NewGuid():N}";
        (await owner.PutAsJsonAsync("/api/v1/me/push-subscriptions", new { endpoint = "https://10.0.0.1/x", keys }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/api/v1/me/push-subscriptions", new { endpoint, keys = new { p256dh = "AAAA", auth = keys.auth } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PutAsJsonAsync("/api/v1/me/push-subscriptions", new { endpoint, keys }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PutAsJsonAsync("/api/v1/me/push-subscriptions", new { endpoint, keys }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK); // renewing is idempotent
        var gone = $"https://fcm.googleapis.com/fcm/send/gone-{Guid.NewGuid():N}";
        (await owner.PutAsJsonAsync("/api/v1/me/push-subscriptions", new { endpoint = gone, keys }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/me/push-subscriptions", Ct)).GetArrayLength().ShouldBe(2);

        // The owner wants handoff SMS; a rule hands a conversation off.
        var prefs = $"/api/v1/workspaces/{ws}/notification-preferences";
        (await owner.PutAsJsonAsync(prefs, new { items = new[] { new { kind = "handoff", inApp = true, push = true, sms = true } } }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/automation/rules", new
        {
            name = "اپراتور",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "اپراتور" } },
            actions = new[] { new { kind = "handoff", reason = "customer_asked" } },
            priority = 0,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await DmAsync(account, "c_" + Guid.NewGuid().ToString("N")[..10], "اپراتور لطفا");
        var list = $"/api/v1/workspaces/{ws}/notifications";
        await Eventually(() => owner.GetFromJsonAsync<JsonElement>(list, Ct), p => p.GetProperty("unreadCount").GetInt32() == 1, "the handoff notification");

        // Push: encrypted for the browser (RFC 8291) and signed with VAPID.
        var watch = Stopwatch.StartNew();
        while (!FakePushService.Deliveries.Any(d => d.Endpoint == endpoint) && watch.Elapsed < TimeSpan.FromSeconds(20))
        {
            await Task.Delay(100, Ct);
        }

        var delivery = FakePushService.Deliveries.Single(d => d.Endpoint == endpoint);
        delivery.Encoding.ShouldBe("aes128gcm");
        delivery.Authorization!.ShouldStartWith("vapid t=");
        delivery.Authorization.ShouldEndWith("k=" + FakePushService.VapidPublicKey);
        var payload = JsonDocument.Parse(browser.Decrypt(delivery.Body)).RootElement;
        payload.GetProperty("kind").GetString().ShouldBe("handoff");
        payload.GetProperty("title").GetString().ShouldBe("یک گفتگو منتظر پاسخ تیم است");
        payload.GetProperty("link").GetString()!.ShouldStartWith("/inbox/");
        payload.GetProperty("workspaceId").GetGuid().ShouldBe(ws);

        // The push service said the second browser is gone: it is dropped.
        await Eventually(() => owner.GetFromJsonAsync<JsonElement>("/api/v1/me/push-subscriptions", Ct), a => a.GetArrayLength() == 1, "the gone subscription dropped");

        // No SMS yet; once the handoff has waited (3 seconds in tests) with no team reply, one SMS.
        (await SmsCountAsync(phone, "منتظر پاسخ تیم")).ShouldBe(0);
        await Eventually(async () => JsonSerializer.SerializeToElement(await SmsCountAsync(phone, "منتظر پاسخ تیم")), n => n.GetInt32() == 1, "the waiting-handoff SMS");

        // Unsubscribing stops push to that browser.
        (await owner.DeleteAsync($"/api/v1/me/push-subscriptions?endpoint={Uri.EscapeDataString(endpoint)}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/me/push-subscriptions", Ct)).GetArrayLength().ShouldBe(0);
    }
}
