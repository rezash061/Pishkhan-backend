using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>Website leads: <c>POST /api/v1/public/leads</c> and the staff list <c>GET /admin/v1/leads</c>.</summary>
[Collection(WorkersCollection.Name)]
public sealed class LeadsTests(InfraFixture infra) : IAsyncLifetime
{
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

    [Fact]
    public async Task Waitlist_and_contact_submissions_are_stored_once_and_listed_for_staff()
    {
        var site = _api.CreateClient();
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        var waitlist = new { type = "waitlist", phone = "+98" + phone[1..], instagramHandle = "@Shop.Test", vertical = "clothing", monthlyDms = "500-2000", utm = new Dictionary<string, string> { ["utm_source"] = "instagram" }, altcha = "x" };

        var first = await site.PostAsJsonAsync("/api/v1/public/leads", waitlist, Ct);
        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await first.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("received");

        // A double submit updates the same lead; a contact form from the same phone is a separate lead.
        (await site.PostAsJsonAsync("/api/v1/public/leads", waitlist with { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);
        (await site.PostAsJsonAsync("/api/v1/public/leads", new { type = "contact", phone, name = "مریم", message = "قیمت پلن رشد؟\nplan: growth", altcha = "x" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        await using (var connection = await _admin.App.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            var stored = await connection.QuerySingleAsync<string>("SELECT phone_enc FROM content.leads WHERE type = 'waitlist' ORDER BY created_at DESC LIMIT 1");
            stored.ShouldNotContain(phone[1..]); // encrypted at rest
        }

        var page = await _admin.StaffClient(StaffRoles.Support).GetFromJsonAsync<JsonElement>("/admin/v1/leads?pageSize=100", Ct);
        var mine = page.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("phone").GetString() == phone).ToList();
        mine.Count.ShouldBe(2);
        var lead = mine.Single(i => i.GetProperty("type").GetString() == "waitlist");
        lead.GetProperty("submissions").GetInt32().ShouldBe(2);
        lead.GetProperty("instagramHandle").GetString().ShouldBe("shop.test");
        lead.GetProperty("utm").GetProperty("utm_source").GetString().ShouldBe("instagram");
        mine.Single(i => i.GetProperty("type").GetString() == "contact").GetProperty("message").GetString()!.ShouldContain("plan: growth");

        var contacts = await _admin.StaffClient(StaffRoles.SuperAdmin).GetFromJsonAsync<JsonElement>("/admin/v1/leads?type=contact", Ct);
        contacts.GetProperty("items").EnumerateArray().ShouldAllBe(i => i.GetProperty("type").GetString() == "contact");
        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/leads", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Invalid_submissions_name_the_fields()
    {
        var site = _api.CreateClient();
        var response = await site.PostAsJsonAsync("/api/v1/public/leads", new { type = "waitlist", phone = "12345", instagramHandle = "bad handle!", utm = new Dictionary<string, string> { ["evil"] = "x" }, altcha = "x" }, Ct);
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var errors = (await response.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        errors.GetProperty("phone")[0].GetString().ShouldBe("validation.phone.invalid");
        errors.GetProperty("instagramHandle")[0].GetString().ShouldBe("validation.instagram.invalid");
        errors.GetProperty("utm")[0].GetString().ShouldBe("validation.invalid");

        var contact = await site.PostAsJsonAsync("/api/v1/public/leads", new { type = "contact", phone = "09121234567", altcha = "x" }, Ct);
        var contactErrors = (await contact.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("errors");
        contactErrors.TryGetProperty("name", out _).ShouldBeTrue();
        contactErrors.TryGetProperty("message", out _).ShouldBeTrue();
    }
}
