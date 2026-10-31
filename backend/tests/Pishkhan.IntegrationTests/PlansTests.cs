using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using MassTransit;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.11: Finance edits versioned plans in the back office; the website's price list follows publishes and archives.</summary>
[Collection(WorkersCollection.Name)]
public sealed class PlansTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay, which carries the cache invalidation to the Api host.</summary>
    private readonly ChannelsHost _workers = workers;
    private ApiFactory _api = null!;
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        _admin = await AdminHost.StartAsync(infra, Ct);

        // The Api host drops its cached price list when the admin host's invalidation arrives over Kafka. Its broadcast consumer
        // starts at the latest offset, so wait until it is connected before the test changes anything.
        var bus = _api.Services.GetRequiredService<IBusControl>();
        var watch = Stopwatch.StartNew();
        while (bus.CheckHealth().Status != BusHealthStatus.Healthy && watch.Elapsed < TimeSpan.FromSeconds(60))
        {
            await Task.Delay(100, Ct);
        }

        bus.CheckHealth().Status.ShouldBe(BusHealthStatus.Healthy);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    private static object Plan(string name, long monthly, Dictionary<string, int?>? limits = null) => new
    {
        name,
        tagline = "برای فروشگاه‌های در حال رشد",
        monthly = new { amount = monthly, currency = "IRR" },
        annual = new { amount = monthly * 10, currency = "IRR" },
        overagePerConversation = new { amount = 9000, currency = "IRR" },
        limits = limits ?? new Dictionary<string, int?> { ["ai_conversations"] = 2000, ["channel_accounts"] = 3, ["seats"] = 5, ["sms"] = null },
        features = new[] { new { key = "receipts", label = "بررسی رسید کارت‌به‌کارت", included = true, limit = "۵۰۰ رسید" } },
        isPublic = true,
        isFree = false,
        highlighted = true,
        sortOrder = 20,
    };

    private async Task<JsonElement?> PublicAsync(string code)
    {
        var plans = await _api.CreateClient().GetFromJsonAsync<JsonElement>("/api/v1/public/plans", Ct);
        return plans.EnumerateArray().Where(p => p.GetProperty("code").GetString() == code).Select(p => (JsonElement?)p).FirstOrDefault();
    }

    private async Task<JsonElement?> EventuallyPublic(string code, Func<JsonElement?, bool> condition, string because)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var plan = await PublicAsync(code);
            if (condition(plan))
            {
                return plan;
            }

            if (watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                throw new TimeoutException("Timed out waiting: " + because);
            }

            await Task.Delay(500, Ct);
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>
    /// Regression: the Redis cache asked the container for HybridCache while HybridCache was being built, which made a second instance;
    /// endpoints read one and the invalidation consumer cleared the other.
    /// </summary>
    [Fact]
    public async Task A_host_holds_one_hybrid_cache()
    {
        (await _api.CreateClient().GetAsync("/api/v1/public/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var direct = _api.Services.GetRequiredService<HybridCache>();
        using var scope = _api.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CacheInvalidationHandler>().ShouldNotBeNull();
        _api.Services.GetServices<HybridCache>().ShouldHaveSingleItem().ShouldBeSameAs(direct);
    }

    [Fact]
    public async Task Plans_are_versioned_drafts_until_published_and_the_price_list_follows()
    {
        var code = "growth-" + Guid.NewGuid().ToString("N")[..6];
        var finance = _admin.StaffClient(StaffRoles.Finance);
        var stepped = _admin.StaffClient(StaffRoles.Finance, stepUp: true);

        // Validation, roles, and a draft that the website does not see.
        (await finance.PostAsJsonAsync("/admin/v1/plans", new { code, plan = Plan("رشد", 12_900_000, new() { ["minutes"] = 5 }) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await finance.PostAsJsonAsync("/admin/v1/plans", new { code = "Bad Code", plan = Plan("رشد", 1) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _admin.StaffClient(StaffRoles.ReadOnly).PostAsJsonAsync("/admin/v1/plans", new { code, plan = Plan("رشد", 1) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var created = await finance.PostAsJsonAsync("/admin/v1/plans", new { code, plan = Plan("رشد", 12_900_000) }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var v1 = await Json(created);
        v1.GetProperty("version").GetInt32().ShouldBe(1);
        v1.GetProperty("status").GetString().ShouldBe("draft");
        v1.GetProperty("limits").GetProperty("sms").ValueKind.ShouldBe(JsonValueKind.Null);
        var v1Id = v1.GetProperty("id").GetGuid();
        (await finance.PostAsJsonAsync("/admin/v1/plans", new { code, plan = Plan("رشد", 1) }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await PublicAsync(code)).ShouldBeNull();

        (await finance.PutAsJsonAsync($"/admin/v1/plans/{v1Id}", Plan("رشد ویژه", 12_900_000), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Publishing needs step-up, then the price list shows it (the list may have been cached before).
        (await finance.PostAsJsonAsync($"/admin/v1/plans/{v1Id}/publish", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var published = await stepped.PostAsJsonAsync($"/admin/v1/plans/{v1Id}/publish", new { reason = "عرضه پلن رشد" }, Ct);
        published.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await Json(published)).GetProperty("status").GetString().ShouldBe("published");
        var onSite = (await EventuallyPublic(code, p => p is not null, "the published plan on the price list"))!.Value;
        onSite.GetProperty("name").GetString().ShouldBe("رشد ویژه");
        onSite.GetProperty("monthly").GetProperty("amount").GetInt64().ShouldBe(12_900_000);
        onSite.GetProperty("monthly").GetProperty("currency").GetString().ShouldBe("IRR");
        onSite.GetProperty("aiConversations").GetInt32().ShouldBe(2000);
        onSite.GetProperty("channelAccounts").GetInt32().ShouldBe(3);
        onSite.GetProperty("seats").GetInt32().ShouldBe(5);
        onSite.GetProperty("features")[0].GetProperty("limit").GetString().ShouldBe("۵۰۰ رسید");

        // A published version is fixed; a new version starts as a copy, one draft at a time.
        (await finance.PutAsJsonAsync($"/admin/v1/plans/{v1Id}", Plan("x", 1), Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var draft = await finance.PostAsync($"/admin/v1/plans/{v1Id}/versions", null, Ct);
        draft.StatusCode.ShouldBe(HttpStatusCode.Created);
        var v2 = await Json(draft);
        v2.GetProperty("version").GetInt32().ShouldBe(2);
        v2.GetProperty("name").GetString().ShouldBe("رشد ویژه");
        var v2Id = v2.GetProperty("id").GetGuid();
        (await finance.PostAsync($"/admin/v1/plans/{v1Id}/versions", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        (await finance.PutAsJsonAsync($"/admin/v1/plans/{v2Id}", Plan("رشد ویژه", 14_900_000), Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await stepped.PostAsJsonAsync($"/admin/v1/plans/{v2Id}/publish", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyPublic(code, p => p?.GetProperty("monthly").GetProperty("amount").GetInt64() == 14_900_000, "the new price");
        (await finance.GetFromJsonAsync<JsonElement>($"/admin/v1/plans/{v1Id}", Ct)).GetProperty("status").GetString().ShouldBe("archived");

        var versions = await _admin.StaffClient(StaffRoles.Support).GetFromJsonAsync<JsonElement>($"/admin/v1/plans?code={code}", Ct);
        versions.GetProperty("totalCount").GetInt32().ShouldBe(2);
        versions.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("version").GetInt32()).ShouldBe([2, 1]);

        // Archiving the published version takes the plan off the list.
        (await stepped.PostAsJsonAsync($"/admin/v1/plans/{v2Id}/archive", new { reason = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stepped.PostAsJsonAsync($"/admin/v1/plans/{v2Id}/archive", new { reason = "پایان فروش این پلن" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        await EventuallyPublic(code, p => p is null, "the archived plan leaving the list");
        (await finance.GetAsync($"/admin/v1/plans/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
