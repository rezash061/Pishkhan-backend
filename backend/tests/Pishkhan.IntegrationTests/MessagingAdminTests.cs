using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>AD-2.4: every SMS is logged without its text, staff find a number's messages, see the provider and Bale state, and send a test SMS.</summary>
[Collection(WorkersCollection.Name)]
public sealed class MessagingAdminTests(InfraFixture infra) : IAsyncLifetime
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
    public async Task Otp_texts_are_logged_masked_and_found_by_phone_and_a_test_sms_needs_super_admin_with_step_up()
    {
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        await _api.SignInAsync(phone, Ct);

        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/platform/sms/log", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var support = _admin.StaffClient(StaffRoles.Support);
        var log = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/platform/sms/log?phone={phone}", Ct);
        log.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var item = log.GetProperty("items")[0];
        item.GetProperty("purpose").GetString().ShouldBe("otp");
        item.GetProperty("status").GetString().ShouldBe("sent");
        item.GetProperty("provider").GetString().ShouldBe("dev");
        item.GetProperty("phone").GetString()!.ShouldContain("***");
        item.GetProperty("phone").GetString()!.ShouldEndWith(phone[^4..]);
        item.TryGetProperty("text", out _).ShouldBeFalse();
        (await support.GetAsync("/admin/v1/platform/sms/log?phone=123", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await support.GetAsync("/admin/v1/platform/sms/log?purpose=spam", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var overview = await support.GetFromJsonAsync<JsonElement>("/admin/v1/platform/messaging", Ct);
        overview.GetProperty("sms").GetProperty("provider").GetProperty("provider").GetString().ShouldBe("dev");
        overview.GetProperty("sms").GetProperty("last24Hours").GetProperty("sent").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        overview.GetProperty("bale").GetProperty("apiBaseUrl").GetString().ShouldNotBeNullOrEmpty();

        var test = new { phone };
        (await support.PostAsJsonAsync("/admin/v1/platform/sms/test", test, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.SuperAdmin).PostAsJsonAsync("/admin/v1/platform/sms/test", test, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var sent = await _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true).PostAsJsonAsync("/admin/v1/platform/sms/test", test, Ct);
        sent.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await sent.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("purpose").GetString().ShouldBe("test");
        (await support.GetFromJsonAsync<JsonElement>($"/admin/v1/platform/sms/log?phone={phone}", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(2);

        (await support.GetFromJsonAsync<JsonElement>("/admin/v1/platform/bale/bots", Ct)).GetProperty("items").ValueKind.ShouldBe(JsonValueKind.Array);
        (await support.PostAsync($"/admin/v1/platform/bale/bots/{Guid.NewGuid()}/check", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
