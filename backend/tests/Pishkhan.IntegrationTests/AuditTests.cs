using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Audit;
using Pishkhan.Modules.Backoffice;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Features;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Hosting;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// Audit log end to end: panel and staff actions → outbox → Kafka → audit sink → owner view and <c>GET /admin/v1/audit</c>.
/// The admin side runs the AdminApi composition in a test server.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AuditTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    /// <summary>Runs the outbox relay and the audit sink.</summary>
    private readonly ChannelsHost _workers = workers;
    private ApiFactory _api = null!;
    private WebApplication _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(infra.Settings);
        builder.WebHost.UseTestServer();
        builder.AddPishkhanWebHost("it-admin-api").AddPostgresCheck();
        builder.Services.AddPishkhanAuthentication(builder.Configuration, builder.Environment, AuthSchemes.Staff);
        builder.Services.AddPishkhanOpenApi(ApiDocuments.Admin);
        builder.Services.AddBackofficeModule(builder.Configuration).AddAuditModule(builder.Configuration).DecorateHandlers();
        builder.Services.AddPishkhanMessaging(builder.Configuration, "it-admin-api", m => m.AddCacheInvalidation());
        _admin = builder.Build();
        _admin.UsePishkhanWebHost();
        _admin.MapBackofficeEndpoints();
        _admin.MapAuditAdminEndpoints();
        await _admin.StartAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    [Fact]
    public async Task Workspace_changes_are_audited_and_the_owner_sees_their_scope()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        (await owner.PatchAsJsonAsync($"/api/v1/workspaces/{ws}", new { name = "فروشگاه تازه" }, Ct)).EnsureSuccessStatusCode();

        var page = await WaitForAsync(owner, $"/api/v1/workspaces/{ws}/audit", 2);
        page.GetProperty("totalCount").GetInt32().ShouldBe(2);
        var items = page.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("action").GetString()).ShouldBe(["workspace.update", "workspace.create"]);
        var update = items[0];
        update.GetProperty("actorType").GetString().ShouldBe("user");
        update.GetProperty("actorName").GetString()!.ShouldContain("*"); // masked phone, resolved by the sink
        update.GetProperty("entityId").GetGuid().ShouldBe(ws);
        update.GetProperty("before").GetProperty("name").GetString().ShouldBe("فروشگاه آزمایشی");
        update.GetProperty("after").GetProperty("name").GetString().ShouldBe("فروشگاه تازه");

        var filtered = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/audit?action=create", Ct);
        filtered.GetProperty("totalCount").GetInt32().ShouldBe(1);

        // Another workspace's owner cannot read this log.
        var stranger = await _api.SignInAsync(Ct);
        (await stranger.GetAsync($"/api/v1/workspaces/{ws}/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // Support sees workspace entries; platform entries (staff sign-ins) only reach SuperAdmin.
        var support = await StaffSignInAsync(StaffRoles.Support);
        var forSupport = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/audit?workspaceId={ws}&pageSize=1", Ct);
        forSupport.GetProperty("totalCount").GetInt32().ShouldBe(2);
        forSupport.GetProperty("items").GetArrayLength().ShouldBe(1);
        forSupport.GetProperty("page").GetInt32().ShouldBe(1);
        forSupport.GetProperty("pageSize").GetInt32().ShouldBe(1);
        (await support.GetFromJsonAsync<JsonElement>("/admin/v1/audit?action=staff.", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Staff_sign_in_is_audited_and_a_missing_code_is_not_a_failure()
    {
        var (username, password) = await ProvisionAsync(StaffRoles.SuperAdmin);
        var admin = await EnrollAsync(username, password);
        var anonymous = _admin.GetTestClient();

        var noCode = await anonymous.PostAsJsonAsync("/admin/v1/auth/login", new { username, password }, Ct);
        noCode.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await noCode.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString().ShouldBe("auth.totp.required");
        (await FailedLoginsAsync(username)).ShouldBe(0);

        var wrong = await anonymous.PostAsJsonAsync("/admin/v1/auth/login", new { username, password, totpCode = "000000" }, Ct);
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await FailedLoginsAsync(username)).ShouldBe(1);

        var page = await WaitForAsync(admin, $"/admin/v1/audit?actor={username}", 3);
        var actions = page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        actions.ShouldBe(["staff.login_failed", "staff.login", "staff.totp_enrolled"]);
        var failed = page.GetProperty("items")[0];
        failed.GetProperty("reason").GetString().ShouldBe("auth.totp.invalid");
        failed.GetProperty("workspaceId").ValueKind.ShouldBe(JsonValueKind.Null);

        // Creating staff is audited with the new account's name and roles, never the password.
        var created = await WaitForAsync(admin, $"/admin/v1/audit?action=staff.create&actor=system", 1);
        var after = created.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("after")).First(a => a.GetProperty("username").GetString() == username);
        after.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe([StaffRoles.SuperAdmin]);
        after.GetRawText().ShouldNotContain(password);
    }

    [Fact]
    public async Task Audit_search_rejects_an_inverted_range_and_needs_an_audit_role()
    {
        var finance = await StaffSignInAsync(StaffRoles.Finance);
        (await finance.GetAsync("/admin/v1/audit", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var support = await StaffSignInAsync(StaffRoles.Support);
        var inverted = await support.GetAsync("/admin/v1/audit?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z", Ct);
        inverted.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    private async Task<JsonElement> WaitForAsync(HttpClient client, string url, int count)
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var page = await client.GetFromJsonAsync<JsonElement>(url, Ct);
            if (page.GetProperty("totalCount").GetInt32() >= count || watch.Elapsed > TimeSpan.FromSeconds(60))
            {
                return page;
            }

            await Task.Delay(200, Ct);
        }
    }

    private async Task<HttpClient> StaffSignInAsync(string role)
    {
        var (username, password) = await ProvisionAsync(role);
        return await EnrollAsync(username, password);
    }

    private async Task<(string Username, string Password)> ProvisionAsync(string role)
    {
        var username = "it-" + Guid.NewGuid().ToString("N")[..10];
        var password = "Pw-" + Guid.NewGuid().ToString("N");
        await using var scope = _admin.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IStaffProvisioning>().CreateAsync(username, password, [role], Ct);
        return (username, password);
    }

    /// <summary>First sign-in: password, then TOTP enrollment with a code computed from the stored secret.</summary>
    private async Task<HttpClient> EnrollAsync(string username, string password)
    {
        var client = _admin.GetTestClient();
        var login = await client.PostAsJsonAsync("/admin/v1/auth/login", new { username, password }, Ct);
        login.EnsureSuccessStatusCode();
        var enrollmentToken = (await login.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("enrollmentToken").GetString();

        byte[] secret;
        await using (var scope = _admin.Services.CreateAsyncScope())
        {
            var staff = await scope.ServiceProvider.GetRequiredService<BackofficeDbContext>().StaffUsers.AsNoTracking().SingleAsync(s => s.Username == username, Ct);
            secret = Convert.FromBase64String(scope.ServiceProvider.GetRequiredService<ISecretProtector>().Unprotect(staff.TotpSecretEnc!, StaffAuthHandlers.TotpPurpose));
        }

        var enroll = await client.PostAsJsonAsync("/admin/v1/auth/totp/enroll", new { enrollmentToken, code = Totp.Compute(secret, DateTimeOffset.UtcNow) }, Ct);
        enroll.EnsureSuccessStatusCode();
        var token = (await enroll.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<int> FailedLoginsAsync(string username)
    {
        await using var scope = _admin.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<BackofficeDbContext>().StaffUsers.AsNoTracking()
            .Where(s => s.Username == username).Select(s => s.FailedLogins).SingleAsync(Ct);
    }
}
