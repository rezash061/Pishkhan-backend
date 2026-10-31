using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.12: staff find a workspace by name, slug, owner phone or Instagram username, and open its detail.</summary>
[Collection(WorkersCollection.Name)]
public sealed class AdminWorkspacesTests(InfraFixture infra) : IAsyncLifetime
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

    private async Task SeedAccountAsync(Guid ws, string username)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var db = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        var externalId = "1789" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999);
        db.Accounts.Add(ChannelAccount.ConnectInstagram(ws, externalId, username, "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
        await db.SaveChangesAsync(Ct);
    }

    private static Guid[] Ids(JsonElement page) => [.. page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    [Fact]
    public async Task Staff_find_workspaces_by_name_slug_owner_phone_and_instagram_and_see_the_detail()
    {
        var marker = Guid.NewGuid().ToString("N")[..8];
        var phone = "0912" + Random.Shared.Next(1_000_000, 9_999_999);
        var owner = await _api.SignInAsync(phone, Ct);
        var created = await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "گالری " + marker, slug = "gallery-" + marker, industry = "jewelry" }, Ct);
        created.EnsureSuccessStatusCode();
        var ws = (await created.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        await SeedAccountAsync(ws, "tala_" + marker);
        var other = await ApiFactory.CreateWorkspaceAsync(await _api.SignInAsync(Ct), Ct);

        var support = _admin.StaffClient(StaffRoles.Support);
        var byName = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q={Uri.EscapeDataString("گالری " + marker)}", Ct);
        Ids(byName).ShouldBe([ws]);
        byName.GetProperty("totalCount").GetInt32().ShouldBe(1);
        var row = byName.GetProperty("items")[0];
        row.GetProperty("status").GetString().ShouldBe("active");
        row.GetProperty("memberCount").GetInt32().ShouldBe(1);
        row.GetProperty("owner").GetProperty("maskedPhone").GetString()!.ShouldContain("***");
        row.GetProperty("channels")[0].GetProperty("username").GetString().ShouldBe("tala_" + marker);
        row.GetProperty("channels")[0].GetProperty("status").GetString().ShouldBe("active");

        // Slug, Instagram username (with @), the owner's phone in another spelling, and the id.
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q=gallery-{marker}", Ct)).ShouldBe([ws]);
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q=%40TALA_{marker}", Ct)).ShouldBe([ws]);
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q=%2B98{phone[1..]}", Ct)).ShouldBe([ws]);
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q={other}", Ct)).ShouldBe([other]);
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q=gallery-{marker}&status=suspended", Ct)).ShouldBeEmpty();
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q=gallery-{marker}&industry=clothing", Ct)).ShouldBeEmpty();

        // Newest first, paged.
        var second = await owner.PostAsJsonAsync("/api/v1/workspaces", new { name = "شعبه " + marker, industry = "jewelry" }, Ct);
        var secondId = (await second.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("id").GetGuid();
        var page = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q={marker}&pageSize=1&page=2", Ct);
        page.GetProperty("totalCount").GetInt32().ShouldBe(2);
        Ids(page).ShouldBe([ws]);
        Ids(await support.GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces?q={marker}&pageSize=1", Ct)).ShouldBe([secondId]);

        var detail = await _admin.StaffClient(StaffRoles.ReadOnly).GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces/{ws}", Ct);
        detail.GetProperty("slug").GetString().ShouldBe("gallery-" + marker);
        detail.GetProperty("industry").GetString().ShouldBe("jewelry");
        var member = detail.GetProperty("members").EnumerateArray().Single();
        member.GetProperty("role").GetString().ShouldBe("owner");
        member.GetProperty("userId").GetGuid().ShouldBe(detail.GetProperty("owner").GetProperty("id").GetGuid());
        detail.GetProperty("channels").GetArrayLength().ShouldBe(1);
        detail.GetProperty("pendingInvitations").GetInt32().ShouldBe(0);

        (await support.GetAsync($"/admin/v1/workspaces/{Guid.NewGuid()}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await support.GetAsync("/admin/v1/workspaces?status=closed", Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await _admin.StaffClient(StaffRoles.ContentEditor).GetAsync("/admin/v1/workspaces", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}
