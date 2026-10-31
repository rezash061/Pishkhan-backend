using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Domain;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Channels.Services;
using Pishkhan.Modules.Files.Domain;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Tenancy.Services;

namespace Pishkhan.IntegrationTests;

/// <summary>Workspace deletion: confirm by name, 30-day grace with cancel, then the purge deletes rows and files and keeps the audit log.</summary>
[Collection(WorkersCollection.Name)]
public sealed class WorkspaceDeletionTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private const string Name = "فروشگاه آزمایشی";

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private async Task<string> SeedAsync(Guid ws)
    {
        await using var scope = workers.Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<ITenantContext>().SetWorkspace(ws);
        var channels = scope.ServiceProvider.GetRequiredService<ChannelsDbContext>();
        var enc = scope.ServiceProvider.GetRequiredService<ISecretProtector>().Protect("tok-ok-" + Guid.NewGuid(), TokenPurposes.Instagram);
        channels.Accounts.Add(ChannelAccount.ConnectInstagram(ws, "1787" + Random.Shared.NextInt64(1_000_000_000, 9_999_999_999), "shop", "Shop", enc, DateTimeOffset.UtcNow.AddDays(60), ["instagram_business_basic"], Guid.NewGuid(), DateTimeOffset.UtcNow));
        await channels.SaveChangesAsync(Ct);

        var files = scope.ServiceProvider.GetRequiredService<FilesDbContext>();
        var file = StoredFile.Receive(Guid.NewGuid(), ws, "product_image", "a.txt", "text/plain", "text/plain", 5, new string('0', 64), Buckets.Quarantine, null, DateTimeOffset.UtcNow);
        files.Files.Add(file);
        await files.SaveChangesAsync(Ct);
        await scope.ServiceProvider.GetRequiredService<IObjectStorage>().PutAsync(Buckets.Quarantine, file.StorageKey, new MemoryStream(Encoding.UTF8.GetBytes("hello")), "text/plain", Ct);
        return file.StorageKey;
    }

    private async Task<long> CountAsync(string table, Guid ws)
    {
        await using var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<long>($"SELECT count(*) FROM {table} WHERE workspace_id = @ws", new { ws });
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
    public async Task Deletion_can_be_cancelled_in_the_grace_period_and_purges_the_data_after_it()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var storageKey = await SeedAsync(ws);
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/automation/rules", new
        {
            name = "قیمت",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "قیمت" } },
            actions = new[] { new { kind = "send_message", message = new { text = "۲۵۰ هزار تومان" } } },
            priority = 0,
        }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Created);

        // The name must match.
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/deletion", new { confirmName = "اشتباه" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var requested = await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/deletion", new { confirmName = Name }, Ct);
        requested.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var body = await requested.Content.ReadFromJsonAsync<JsonElement>(Ct);
        body.GetProperty("status").GetString().ShouldBe("pending_deletion");
        body.GetProperty("deletionScheduledFor").GetDateTimeOffset().ShouldBeInRange(DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(5));

        // Closed for everyone, but still listed for the owner to cancel; strangers cannot cancel.
        (await owner.GetAsync($"/api/v1/workspaces/{ws}", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var listed = (await owner.GetFromJsonAsync<JsonElement>("/api/v1/workspaces", Ct)).EnumerateArray().Single(w => w.GetProperty("id").GetGuid() == ws);
        listed.GetProperty("status").GetString().ShouldBe("pending_deletion");
        var stranger = await _api.SignInAsync(Ct);
        (await stranger.DeleteAsync($"/api/v1/workspaces/{ws}/deletion", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var cancelled = await owner.DeleteAsync($"/api/v1/workspaces/{ws}/deletion", Ct);
        cancelled.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await cancelled.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("active");
        (await owner.GetAsync($"/api/v1/workspaces/{ws}", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}/deletion", Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        // Ask again, and let the grace period pass.
        (await owner.PostAsJsonAsync($"/api/v1/workspaces/{ws}/deletion", new { confirmName = Name }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await using (var connection = await workers.Host.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(Ct))
        {
            await connection.ExecuteAsync("UPDATE tenancy.workspaces SET deletion_requested_at = now() - interval '31 days' WHERE id = @ws", new { ws });
        }

        await using (var scope = workers.Host.Services.CreateAsyncScope())
        {
            var job = ActivatorUtilities.CreateInstance<WorkspacePurgeJob>(scope.ServiceProvider);
            (await job.RunAsync(Ct)).ShouldBeGreaterThanOrEqualTo(1);
        }

        await Eventually(async () => await CountAsync("channels.channel_accounts", ws) == 0 && await CountAsync("automation.rules", ws) == 0
            && await CountAsync("tenancy.memberships", ws) == 0 && await CountAsync("files.files", ws) == 0, "the purge");
        var storage = workers.Host.Services.GetRequiredService<IObjectStorage>();
        (await storage.StatAsync(Buckets.Quarantine, storageKey, Ct)).ShouldBeNull();
        await Eventually(async () => await CountAsync("audit.audit_entries", ws) >= 3, "the audit entries, which are kept");

        (await owner.GetFromJsonAsync<JsonElement>("/api/v1/workspaces", Ct)).EnumerateArray().ShouldNotContain(w => w.GetProperty("id").GetGuid() == ws);
        (await owner.DeleteAsync($"/api/v1/workspaces/{ws}/deletion", Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }
}
