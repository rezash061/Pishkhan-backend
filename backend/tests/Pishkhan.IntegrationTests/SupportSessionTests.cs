using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Web;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>Support sessions: step-up and a reason in the back office, a one-time link, a 30-minute token with limits, audited for the owner.</summary>
[Collection(WorkersCollection.Name)]
public sealed class SupportSessionTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay and the audit sink.</summary>
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

    private HttpClient Staff(string role, bool stepUp)
    {
        List<Claim> claims = [new(PishkhanClaims.Roles, role), new(PishkhanClaims.AuthMethods, "pwd+otp")];
        if (stepUp)
        {
            claims.Add(new Claim("step_up_at", DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
        }

        var token = _admin.App.Services.GetRequiredService<TokenFactory>().CreateAccessToken(AuthSchemes.Staff, Uuid.NewV7(), Uuid.NewV7(), claims);
        var client = _admin.App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    [Fact]
    public async Task A_support_session_needs_step_up_works_once_is_limited_and_shows_in_the_owners_audit_log()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var grants = $"/admin/v1/workspaces/{ws}/support-sessions";
        var request = new { reason = "تیکت ۱۲۴۵: قانون پاسخ خودکار کار نمی‌کند" };

        (await Staff(StaffRoles.Support, stepUp: false).PostAsJsonAsync(grants, request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Staff(StaffRoles.Finance, stepUp: true).PostAsJsonAsync(grants, request, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await Staff(StaffRoles.Support, stepUp: true).PostAsJsonAsync(grants, new { reason = "" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var granted = await Staff(StaffRoles.Support, stepUp: true).PostAsJsonAsync(grants, request, Ct);
        granted.StatusCode.ShouldBe(HttpStatusCode.OK);
        var url = new Uri((await granted.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("url").GetString()!);
        url.AbsolutePath.ShouldBe("/api/auth/impersonation");
        var token = HttpUtility.ParseQueryString(url.Query)["token"]!;

        // The panel exchanges it once.
        var anonymous = _api.CreateClient();
        var exchanged = await anonymous.PostAsJsonAsync("/api/v1/auth/support-session", new { token }, Ct);
        exchanged.StatusCode.ShouldBe(HttpStatusCode.OK);
        var session = await exchanged.Content.ReadFromJsonAsync<JsonElement>(Ct);
        session.GetProperty("workspaceId").GetGuid().ShouldBe(ws);
        session.GetProperty("reason").GetString().ShouldBe(request.reason);
        session.GetProperty("accessTokenExpiresAt").GetDateTimeOffset().ShouldBeLessThanOrEqualTo(DateTimeOffset.UtcNow.AddMinutes(31));
        (await anonymous.PostAsJsonAsync("/api/v1/auth/support-session", new { token }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var support = _api.CreateClient();
        support.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.GetProperty("accessToken").GetString());
        (await support.GetAsync($"/api/v1/workspaces/{ws}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await support.PatchAsJsonAsync($"/api/v1/workspaces/{ws}", new { name = "نام تازه" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        var denied = await support.PutAsJsonAsync("/api/v1/me/password", new { newPassword = "a-long-password-1" }, Ct);
        denied.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await denied.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString().ShouldBe("auth.support_session.not_allowed");

        // The owner sees the grant, the start and the change, each by «support».
        var watch = Stopwatch.StartNew();
        List<JsonElement> entries;
        while (true)
        {
            var page = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/audit?pageSize=50", Ct);
            entries = [.. page.GetProperty("items").EnumerateArray()];
            if (new[] { "support_session.grant", "support_session.start", "workspace.update" }.All(a => entries.Any(e => e.GetProperty("action").GetString() == a)) || watch.Elapsed > TimeSpan.FromSeconds(45))
            {
                break;
            }

            await Task.Delay(300, Ct);
        }

        var update = entries.Single(e => e.GetProperty("action").GetString() == "workspace.update");
        update.GetProperty("actorType").GetString().ShouldBe("staff");
        update.GetProperty("actorName").GetString().ShouldBe("support");
        entries.Single(e => e.GetProperty("action").GetString() == "support_session.start").GetProperty("reason").GetString().ShouldBe(request.reason);

        // Ending the session revokes its token.
        (await support.PostAsync("/api/v1/auth/logout", null, Ct)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await support.GetAsync($"/api/v1/workspaces/{ws}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await owner.GetAsync($"/api/v1/workspaces/{ws}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
