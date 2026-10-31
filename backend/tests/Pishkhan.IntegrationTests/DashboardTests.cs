using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Analytics.Persistence;
using Pishkhan.Modules.Channels.Contracts;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.10: DMs, rule and team replies and a handoff reach the hourly rollups once each, and the dashboard reports them.</summary>
[Collection(WorkersCollection.Name)]
public sealed class DashboardTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<(Guid Workspace, string AccountExternalId)> SeedAccountAsync(Guid ws)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1786" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        db.Accounts.Add(ChannelAccount.ConnectInstagram(ws, externalId, "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
        return (ws, externalId);
    }

    private async Task DmAsync(string accountExternalId, string customer, string text)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var entry = JsonSerializer.SerializeToElement(new
        {
            id = accountExternalId,
            time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            messaging = new[]
            {
                new
                {
                    sender = new { id = customer },
                    recipient = new { id = accountExternalId },
                    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    message = new { mid = "m_" + Guid.NewGuid().ToString("N"), text },
                },
            },
        });
        db.Publish(new InstagramWebhookReceived("instagram", accountExternalId, entry, DateTimeOffset.UtcNow), accountExternalId);
        await db.SaveChangesAsync(Ct);
    }

    private async Task<(int Received, int New, int Rule, int Human, int Handoffs)> RollupAsync(Guid ws)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var rows = await scope.ServiceProvider.GetRequiredService<AnalyticsDbContext>().Hourly.AsNoTracking().ToListAsync(Ct);
        return (rows.Sum(r => r.MessagesReceived), rows.Sum(r => r.NewConversations), rows.Sum(r => r.RepliesByRule), rows.Sum(r => r.RepliesByHuman), rows.Sum(r => r.Handoffs));
    }

    private static async Task Eventually(Func<Task<bool>> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (!await condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Messages_replies_and_handoffs_are_counted_once_and_shown_on_the_dashboard()
    {
        var client = await _api.SignInAsync(Ct);
        var (ws, account) = await SeedAccountAsync(await ApiFactory.CreateWorkspaceAsync(client, Ct));
        var rules = $"/api/v1/workspaces/{ws}/automation/rules";
        (await client.PostAsJsonAsync(rules, new
        {
            name = "قیمت",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "قیمت" } },
            actions = new[] { new { kind = "send_message", message = new { text = "۲۵۰ هزار تومان" } } },
            priority = 0,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await client.PostAsJsonAsync(rules, new
        {
            name = "اپراتور",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "اپراتور" } },
            actions = new[] { new { kind = "handoff", reason = "customer_asked" } },
            priority = 0,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var sara = "c_" + Guid.NewGuid().ToString("N")[..10];
        var ali = "c_" + Guid.NewGuid().ToString("N")[..10];
        await DmAsync(account, sara, "قیمت مانتو؟");
        await DmAsync(account, ali, "اپراتور لطفا");
        await Eventually(async () => await RollupAsync(ws) is { Received: 2, New: 2, Rule: 1, Handoffs: 1 }, "rollups of the two DMs");

        var list = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/conversations", Ct);
        var aliConversation = list.GetProperty("items").EnumerateArray().First(c => c.GetProperty("status").GetString() == "handed_off").GetProperty("id").GetGuid();
        (await client.PostAsJsonAsync($"/api/v1/workspaces/{ws}/conversations/{aliConversation}/messages", new { text = "سلام، در خدمتم" }, Ct)).IsSuccessStatusCode.ShouldBeTrue();
        await Eventually(async () => await RollupAsync(ws) is { Human: 1 }, "the team reply");

        // Each event is counted once (the handlers are idempotent), so nothing drifts after the last one arrives.
        await Task.Delay(1000, Ct);
        (await RollupAsync(ws)).ShouldBe((2, 2, 1, 1, 1));

        var dashboard = await client.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/dashboard?range=7d", Ct);
        var totals = dashboard.GetProperty("totals");
        totals.GetProperty("messagesReceived").GetInt32().ShouldBe(2);
        totals.GetProperty("newConversations").GetInt32().ShouldBe(2);
        totals.GetProperty("repliesByRule").GetInt32().ShouldBe(1);
        totals.GetProperty("repliesByHuman").GetInt32().ShouldBe(1);
        totals.GetProperty("handoffs").GetInt32().ShouldBe(1);
        totals.GetProperty("automatedShare").GetDouble().ShouldBe(0.5);
        dashboard.GetProperty("days").GetArrayLength().ShouldBe(7);
        dashboard.GetProperty("days")[6].GetProperty("messagesReceived").GetInt32().ShouldBe(2);
        dashboard.GetProperty("inbox").GetProperty("handedOff").GetInt32().ShouldBe(1);
        dashboard.GetProperty("inbox").GetProperty("open").GetInt32().ShouldBe(1);
        var channel = dashboard.GetProperty("channels").EnumerateArray().Single();
        channel.GetProperty("status").GetString().ShouldBe(ChannelStatuses.Active);
        channel.GetProperty("healthy").GetBoolean().ShouldBeTrue();

        (await client.GetAsync($"/api/v1/workspaces/{ws}/dashboard?range=year", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }
}
