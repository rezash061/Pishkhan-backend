using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.12: staff find a panel user, see their workspaces and devices, sign them out everywhere, block and unblock them.</summary>
[Collection(WorkersCollection.Name)]
public sealed class AdminUsersTests(InfraFixture infra) : IAsyncLifetime
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
    public async Task Staff_find_a_user_end_their_sessions_and_block_and_unblock_them()
    {
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        var user = await _api.SignInAsync(phone, Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(user, Ct);
        await Task.Delay(1100, Ct); // the OTP resend cooldown (1 s in tests)
        var secondDevice = await _api.SignInAsync(phone, Ct);

        var support = _admin.StaffClient(StaffRoles.Support);
        var found = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/users?q=%2B98{phone[1..]}", Ct);
        found.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var row = found.GetProperty("items")[0];
        row.GetProperty("maskedPhone").GetString()!.ShouldContain("***");
        row.GetProperty("status").GetString().ShouldBe("active");
        row.GetProperty("activeSessions").GetInt32().ShouldBe(2);
        var id = row.GetProperty("id").GetGuid();

        var detail = await _admin.StaffClient(StaffRoles.ReadOnly).GetFromJsonAsync<JsonElement>($"/admin/v1/users/{id}", Ct);
        var membership = detail.GetProperty("workspaces").EnumerateArray().Single();
        membership.GetProperty("workspaceId").GetGuid().ShouldBe(ws);
        membership.GetProperty("role").GetString().ShouldBe("owner");
        detail.GetProperty("sessions").GetArrayLength().ShouldBe(2);
        detail.GetProperty("totpEnabled").GetBoolean().ShouldBeFalse();

        // Actions need Support, a fresh step-up and a reason.
        var reason = new { reason = "درخواست مالک حساب" };
        (await support.PostAsJsonAsync($"/admin/v1/users/{id}/sessions/revoke", reason, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.ReadOnly, stepUp: true).PostAsJsonAsync($"/admin/v1/users/{id}/block", reason, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var stepped = _admin.StaffClient(StaffRoles.Support, stepUp: true);
        (await stepped.PostAsJsonAsync($"/admin/v1/users/{id}/block", new { reason = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await stepped.PostAsJsonAsync($"/admin/v1/users/{Guid.NewGuid()}/block", reason, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Signing out everywhere ends both devices at once.
        (await user.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var revoked = await stepped.PostAsJsonAsync($"/admin/v1/users/{id}/sessions/revoke", reason, Ct);
        revoked.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await revoked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("activeSessions").GetInt32().ShouldBe(0);
        (await user.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await secondDevice.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        // Blocked: sign-in is refused. Unblocked: it works again.
        var blocked = await stepped.PostAsJsonAsync($"/admin/v1/users/{id}/block", reason, Ct);
        (await blocked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("suspended");
        (await support.GetFromJsonAsync<JsonElement>($"/admin/v1/users?q={id}&status=suspended", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);
        await Task.Delay(1100, Ct);
        await Should.ThrowAsync<HttpRequestException>(() => _api.SignInAsync(phone, Ct));

        var unblocked = await stepped.PostAsJsonAsync($"/admin/v1/users/{id}/unblock", reason, Ct);
        (await unblocked.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("active");
        await Task.Delay(1100, Ct);
        var again = await _api.SignInAsync(phone, Ct);
        (await again.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await support.GetAsync("/admin/v1/users?status=gone", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _admin.StaffClient(StaffRoles.ContentEditor).GetAsync("/admin/v1/users", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
