using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>AD-2.6 / UP-1.18: sellers open tickets, support answers from the back office (the seller is notified), and announcements show in the panel.</summary>
[Collection(WorkersCollection.Name)]
public sealed class SupportTicketsTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay and the notification consumers.</summary>
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
        await _admin.DisposeAsync();
        await _api.DisposeAsync();
    }

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
    public async Task A_seller_ticket_is_answered_by_support_who_notify_the_seller_and_closing_works_on_both_sides()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var tickets = $"/api/v1/workspaces/{ws}/support/tickets";

        (await owner.PostAsJsonAsync(tickets, new { subject = "ab", category = "billing", body = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await owner.PostAsJsonAsync(tickets, new { subject = "پرداخت", category = "nope", body = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var opened = await owner.PostAsJsonAsync(tickets, new { subject = "پرداخت اشتراک انجام نشد", category = "billing", body = "مبلغ کم شد ولی اشتراک فعال نشد." }, Ct);
        opened.StatusCode.ShouldBe(HttpStatusCode.Created);
        var detail = await opened.Content.ReadFromJsonAsync<JsonElement>(Ct);
        var ticket = detail.GetProperty("ticket");
        var id = ticket.GetProperty("id").GetGuid();
        var number = ticket.GetProperty("number").GetInt64();
        number.ShouldBeGreaterThan(1000);
        ticket.GetProperty("status").GetString().ShouldBe("open");
        detail.GetProperty("messages").GetArrayLength().ShouldBe(1);

        // Another workspace cannot see it.
        var stranger = await _api.SignInAsync(Ct);
        var otherWs = await ApiFactory.CreateWorkspaceAsync(stranger, Ct);
        (await stranger.GetAsync($"/api/v1/workspaces/{otherWs}/support/tickets/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Staff: Finance cannot read the queue; ReadOnly can read but not answer.
        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/support/tickets", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var readOnly = _admin.StaffClient(StaffRoles.ReadOnly);
        var queue = await readOnly.GetFromJsonAsync<JsonElement>($"/admin/v1/support/tickets?workspaceId={ws}&status=open", Ct);
        queue.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid().ShouldBe(id);
        (await readOnly.GetFromJsonAsync<JsonElement>($"/admin/v1/support/tickets?q={number}", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
        (await readOnly.PostAsJsonAsync($"/admin/v1/support/tickets/{id}/messages", new { body = "سلام" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var support = _admin.StaffClient(StaffRoles.Support);
        (await support.GetFromJsonAsync<JsonElement>("/admin/v1/support/tickets/counts", Ct)).GetProperty("open").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        var answered = await support.PostAsJsonAsync($"/admin/v1/support/tickets/{id}/messages", new { body = "پرداخت برگشت داده شد و اشتراک فعال است." }, Ct);
        answered.StatusCode.ShouldBe(HttpStatusCode.OK);
        var staffView = (await answered.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("ticket");
        staffView.GetProperty("status").GetString().ShouldBe("answered");
        staffView.GetProperty("assignedTo").ValueKind.ShouldBe(JsonValueKind.String);
        (await support.GetFromJsonAsync<JsonElement>("/admin/v1/support/tickets?assigned=me", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);

        // The seller sees the answer under Pishkhan's name and is notified.
        var mine = await owner.GetFromJsonAsync<JsonElement>($"{tickets}/{id}", Ct);
        mine.GetProperty("ticket").GetProperty("status").GetString().ShouldBe("answered");
        var reply = mine.GetProperty("messages")[1];
        reply.GetProperty("authorKind").GetString().ShouldBe("staff");
        reply.GetProperty("authorName").GetString().ShouldBe("پشتیبانی پیشخوان");
        reply.GetProperty("authorId").ValueKind.ShouldBe(JsonValueKind.Null);
        var notifications = await Eventually(
            () => owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/notifications", Ct),
            p => p.GetProperty("items").EnumerateArray().Any(n => n.GetProperty("kind").GetString() == "support"),
            "the support reply notification");
        notifications.GetProperty("items").EnumerateArray().First(n => n.GetProperty("kind").GetString() == "support").GetProperty("link").GetString()
            .ShouldBe($"/support/{id}");

        // The seller writes again (open), closes it; staff reopen it.
        (await owner.PostAsJsonAsync($"{tickets}/{id}/messages", new { body = "ممنون، درست شد." }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var closed = await owner.PostAsync($"{tickets}/{id}/close", null, Ct);
        (await closed.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("closed");
        (await owner.GetFromJsonAsync<JsonElement>($"{tickets}?status=closed", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
        var reopened = await support.PostAsync($"/admin/v1/support/tickets/{id}/reopen", null, Ct);
        (await reopened.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("open");
        (await support.PostAsJsonAsync($"/admin/v1/support/tickets/{id}/assign", new { staffId = (Guid?)null }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Announcements_show_in_the_panel_while_active_and_stop_when_ended()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var support = _admin.StaffClient(StaffRoles.Support);
        var title = "به‌روزرسانی " + Guid.NewGuid().ToString("N")[..6];

        (await _admin.StaffClient(StaffRoles.Finance).PostAsJsonAsync("/admin/v1/announcements", new { title, severity = "info" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.Forbidden);
        (await support.PostAsJsonAsync("/admin/v1/announcements", new { title, severity = "loud" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await support.PostAsJsonAsync("/admin/v1/announcements", new { title, severity = "info", link = "javascript:alert(1)" }, Ct)).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);

        var created = await support.PostAsJsonAsync("/admin/v1/announcements", new { title, body = "امشب ساعت ۲ تا ۳", severity = "warning", link = "/billing" }, Ct);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var announcement = await created.Content.ReadFromJsonAsync<JsonElement>(Ct);
        announcement.GetProperty("state").GetString().ShouldBe("active");
        var id = announcement.GetProperty("id").GetGuid();

        var later = await support.PostAsJsonAsync("/admin/v1/announcements", new { title = title + " بعدی", severity = "info", startsAt = DateTimeOffset.UtcNow.AddDays(1) }, Ct);
        (await later.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString().ShouldBe("scheduled");

        var banner = $"/api/v1/workspaces/{ws}/announcements";
        var shown = await owner.GetFromJsonAsync<JsonElement>(banner, Ct);
        shown.EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldContain(title);
        shown.EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldNotContain(title + " بعدی");

        var ended = await support.PostAsync($"/admin/v1/announcements/{id}/end", null, Ct);
        (await ended.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("state").GetString().ShouldBe("ended");
        (await owner.GetFromJsonAsync<JsonElement>(banner, Ct)).EnumerateArray().Select(a => a.GetProperty("title").GetString()).ShouldNotContain(title);
        (await support.DeleteAsync($"/admin/v1/announcements/{id}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
