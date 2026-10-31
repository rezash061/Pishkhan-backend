using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Billing.Contracts;
using Pishkhan.Modules.Billing.Persistence;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.11: the free plan by default, plans and trials granted by Finance, usage per period and 402 at the plan's limits.</summary>
[Collection(WorkersCollection.Name)]
public sealed class SubscriptionsTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay and the usage consumer.</summary>
    private readonly ChannelsHost _workers = workers;
    private ApiFactory _api = null!;
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        _admin = await AdminHost.StartAsync(infra, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    private async Task<Guid> PublishedPlanAsync(string code, bool isFree, bool isPublic, Dictionary<string, int?> limits)
    {
        var finance = _admin.StaffClient(StaffRoles.Finance, stepUp: true);
        var created = await finance.PostAsJsonAsync("/admin/v1/plans", new
        {
            code,
            plan = new
            {
                name = isFree ? "رایگان" : "تیم",
                monthly = new { amount = isFree ? 0 : 5_000_000, currency = "IRR" },
                limits,
                isPublic,
                isFree,
                highlighted = false,
                sortOrder = 90,
            },
        }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        (await finance.PostAsJsonAsync($"/admin/v1/plans/{id}/publish", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        return id;
    }

    private async Task RecordUsageAsync(Guid ws, string meter, int quantity, Guid? subject = null)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<BillingDbContext>();
        db.Publish(new UsageRecorded(ws, meter, quantity, DateTimeOffset.UtcNow, subject), ws.ToString(), workspaceId: ws);
        await db.SaveChangesAsync(Ct);
    }

    private static long Used(JsonElement usage, string meter) =>
        usage.GetProperty("meters").EnumerateArray().First(m => m.GetProperty("meter").GetString() == meter).GetProperty("used").GetInt64();

    [Fact]
    public async Task Finance_grants_plans_and_trials_the_panel_shows_limits_and_usage_and_limits_answer_402()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        var freeId = await PublishedPlanAsync("free-" + marker, isFree: true, isPublic: true, new());
        var teamId = await PublishedPlanAsync("team-" + marker, isFree: false, isPublic: false, new()
        {
            [BillingMeters.Seats] = 2, [BillingMeters.ChannelAccounts] = 0, [BillingMeters.AiConversations] = 100, [BillingMeters.AiMessages] = null,
        });

        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);

        // No subscription: the free plan.
        var current = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/billing/subscription", Ct);
        current.GetProperty("source").GetString().ShouldBe("free");
        current.GetProperty("planId").GetGuid().ShouldBe(freeId);
        current.GetProperty("subscription").ValueKind.ShouldBe(JsonValueKind.Null);

        // Granting needs Finance and step-up, a published plan, and an end for a trial.
        var finance = _admin.StaffClient(StaffRoles.Finance, stepUp: true);
        var grant = new { planId = teamId, trial = true, days = 14, reason = "آزمایش بتای بسته" };
        (await _admin.StaffClient(StaffRoles.Finance).PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription", grant, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.Support, stepUp: true).PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription", grant, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription", grant with { days = 0 }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription", new { planId = teamId, trial = true, reason = "آزمایش بتای بسته" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{Guid.NewGuid()}/subscription", grant, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription/extend", new { days = 7, reason = "تمدید آزمایشی" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var granted = await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription", grant, Ct);
        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var trial = await granted.Content.ReadFromJsonAsync<JsonElement>(Ct);
        trial.GetProperty("state").GetString().ShouldBe("trialing");
        var trialEnd = trial.GetProperty("endsAt").GetDateTimeOffset();
        trialEnd.ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(13.9), DateTimeOffset.UtcNow.AddDays(14.1));

        current = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/billing/subscription", Ct);
        current.GetProperty("source").GetString().ShouldBe("subscription");
        current.GetProperty("planCode").GetString().ShouldBe("team-" + marker);
        current.GetProperty("trial").GetBoolean().ShouldBeTrue();
        current.GetProperty("limits").GetProperty("seats").GetInt32().ShouldBe(2);

        // The back office's workspace detail shows the same plan.
        var detail = await _admin.StaffClient(StaffRoles.Support).GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces/{ws}", Ct);
        detail.GetProperty("planId").GetGuid().ShouldBe(teamId);
        detail.GetProperty("plan").GetProperty("source").GetString().ShouldBe("subscription");
        detail.GetProperty("plan").GetProperty("code").GetString().ShouldBe("team-" + marker);
        detail.GetProperty("plan").GetProperty("trial").GetBoolean().ShouldBeTrue();

        // Seats: the owner and one invitation fill the plan; Instagram: the plan has no accounts.
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/invitations", new { phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999), role = "agent" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        var over = await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/invitations", new { phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999), role = "agent" }, Ct);
        over.StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);
        (await over.Content.ReadAsStringAsync(Ct)).ShouldContain("billing.plan_limit");
        (await owner.PostAsync($"/api/v1/workspaces/{ws}/channels/instagram/connect", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.PaymentRequired);

        // Usage: AI conversations count once per conversation and period.
        var conversation = Guid.NewGuid();
        await RecordUsageAsync(ws, BillingMeters.AiMessages, 3);
        await RecordUsageAsync(ws, BillingMeters.AiConversations, 1, conversation);
        await RecordUsageAsync(ws, BillingMeters.AiConversations, 1, conversation);
        await RecordUsageAsync(ws, BillingMeters.AiConversations, 1, Guid.NewGuid());
        var watch = Stopwatch.StartNew();
        JsonElement usage;
        while (true)
        {
            usage = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/billing/usage", Ct);
            if (Used(usage, BillingMeters.AiMessages) == 3 && Used(usage, BillingMeters.AiConversations) == 2)
            {
                break;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "usage events are counted");
            await Task.Delay(300, Ct);
        }

        Used(usage, BillingMeters.Seats).ShouldBe(2);
        Used(usage, BillingMeters.ChannelAccounts).ShouldBe(0);
        usage.GetProperty("meters").EnumerateArray().First(m => m.GetProperty("meter").GetString() == BillingMeters.AiConversations).GetProperty("limit").GetInt32().ShouldBe(100);
        usage.GetProperty("periodEnd").GetDateTimeOffset().ShouldBeGreaterThan(usage.GetProperty("periodStart").GetDateTimeOffset().AddDays(28));

        // Extend, then cancel: back on the free plan, with the history in the back office.
        var extended = await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription/extend", new { days = 7, reason = "تمدید دوره آزمایشی" }, Ct);
        extended.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await extended.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("endsAt").GetDateTimeOffset().ShouldBe(trialEnd.AddDays(7), TimeSpan.FromSeconds(1));
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription/cancel", new { reason = "پایان بتای آزمایشی" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await finance.PostAsJsonAsync($"/admin/v1/workspaces/{ws}/subscription/cancel", new { reason = "پایان بتای آزمایشی" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var deadline = Stopwatch.StartNew();
        while ((await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/billing/subscription", Ct)).GetProperty("source").GetString() != "free")
        {
            deadline.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(45), "the cancellation reaches the Api host's cache");
            await Task.Delay(300, Ct);
        }

        var billing = await _admin.StaffClient(StaffRoles.ReadOnly).GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces/{ws}/billing", Ct);
        billing.GetProperty("current").GetProperty("source").GetString().ShouldBe("free");
        var history = billing.GetProperty("history");
        history.GetArrayLength().ShouldBe(1);
        history[0].GetProperty("state").GetString().ShouldBe("ended");
        history[0].GetProperty("endReason").GetString().ShouldBe("cancelled");
        Used(billing.GetProperty("usage"), BillingMeters.AiMessages).ShouldBe(3);

        // Leave no free plan behind for other tests.
        (await finance.PostAsJsonAsync($"/admin/v1/plans/{freeId}/archive", new { reason = "پایان آزمون" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
