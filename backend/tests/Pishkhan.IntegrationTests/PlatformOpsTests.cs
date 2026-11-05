using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>AD-1.6 to AD-1.8: quarantine, dead letters, Kafka lag, workers and feature flags in the AdminApi.</summary>
[Collection(WorkersCollection.Name)]
public sealed class PlatformOpsTests(InfraFixture infra) : IAsyncLifetime
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

    private async Task ExecAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = new NpgsqlConnection(infra.PostgresConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(Ct);
    }

    private async Task<Guid> SeedFileAsync(Guid workspaceId, string status, TimeSpan age, string? signature = null)
    {
        var id = Guid.NewGuid();
        await ExecAsync(
            """
            INSERT INTO files.files(id, workspace_id, purpose, original_name, content_type_detected, size, sha256, bucket, storage_key, status, signature, scan_attempts, derived, created_at, updated_at)
            VALUES (@id, @ws, 'product_image', 'x.jpg', 'image/jpeg', 10, @sha, 'quarantine', @key, @status, @sig, 0, '{}', now() - @age, now())
            """,
            ("id", id), ("ws", workspaceId), ("sha", id.ToString("N")), ("key", $"{workspaceId:N}/{id:N}"), ("status", status), ("sig", (object?)signature ?? DBNull.Value), ("age", age));
        return id;
    }

    [Fact]
    public async Task Support_lists_quarantined_files_and_rescans_deletes_or_clears_them()
    {
        var user = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(user, Ct);
        var infected = await SeedFileAsync(ws, "infected", TimeSpan.FromHours(1), "Eicar-Test-Signature");
        var failed = await SeedFileAsync(ws, "scan_failed", TimeSpan.FromHours(2));
        var stuck = await SeedFileAsync(ws, "pending_scan", TimeSpan.FromMinutes(30));
        await SeedFileAsync(ws, "pending_scan", TimeSpan.Zero); // fresh: not listed

        var support = _admin.StaffClient(StaffRoles.Support);
        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/files/quarantine", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var list = await support.GetFromJsonAsync<JsonElement>($"/admin/v1/files/quarantine?workspaceId={ws}", Ct);
        list.GetProperty("totalCount").GetInt32().ShouldBe(3);
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == infected);
        row.GetProperty("signature").GetString().ShouldBe("Eicar-Test-Signature");
        row.TryGetProperty("url", out _).ShouldBeFalse();
        (await support.GetFromJsonAsync<JsonElement>($"/admin/v1/files/quarantine?workspaceId={ws}&status=pending", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(1);

        (await support.PostAsJsonAsync($"/admin/v1/files/{failed}/rescan", new { reason = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var rescanned = await support.PostAsJsonAsync($"/admin/v1/files/{failed}/rescan", new { reason = "ClamAV was updated" }, Ct);
        rescanned.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await rescanned.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("status").GetString().ShouldBe("pending_scan");

        // False positive: SuperAdmin with step-up only; the file goes back to scanning, it is not released.
        (await _admin.StaffClient(StaffRoles.Support, stepUp: true).PostAsJsonAsync($"/admin/v1/files/{infected}/false-positive", new { reason = "known clean sample" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var super = _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true);
        var cleared = await (await super.PostAsJsonAsync($"/admin/v1/files/{infected}/false-positive", new { reason = "known clean sample" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        cleared.GetProperty("status").GetString().ShouldBe("pending_scan");
        cleared.GetProperty("falsePositiveAt").ValueKind.ShouldBe(JsonValueKind.String);

        (await support.PostAsJsonAsync($"/admin/v1/files/{stuck}/delete", new { reason = "stuck upload" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.Support, stepUp: true).PostAsJsonAsync($"/admin/v1/files/{stuck}/delete", new { reason = "stuck upload" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await _admin.StaffClient(StaffRoles.Support, stepUp: true).PostAsJsonAsync($"/admin/v1/files/{stuck}/delete", new { reason = "stuck upload" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var summary = await support.GetFromJsonAsync<JsonElement>("/admin/v1/files/quarantine/summary", Ct);
        summary.TryGetProperty("infected", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Dead_letters_are_listed_replayed_to_the_failed_consumer_and_discarded()
    {
        var messageId = Guid.NewGuid();
        var replayId = Guid.NewGuid();
        var discardId = Guid.NewGuid();
        var envelope = JsonSerializer.Serialize(new
        {
            messageId,
            type = "files.scan-requested",
            occurredAt = DateTimeOffset.UtcNow,
            correlationId = Guid.NewGuid(),
            schemaVersion = 1,
            payload = new { fileId = Guid.NewGuid(), workspaceId = Guid.NewGuid() },
            deadLetter = new { originalTopic = "files.scan-requested.v1", consumerGroup = "media", consumer = "Some.Handler", exceptionType = "System.Exception", exceptionMessage = "boom", attempts = 5, failedAt = DateTimeOffset.UtcNow },
        });
        foreach (var id in new[] { replayId, discardId })
        {
            await ExecAsync(
                """
                INSERT INTO platform.dead_letters(id, message_id, topic, consumer_group, consumer, key, type, envelope, exception_type, exception_message, attempts, failed_at, status)
                VALUES (@id, @mid, 'files.scan-requested.v1', 'media', 'Some.Handler', 'k', 'files.scan-requested', @env::jsonb, 'System.Exception', 'boom', 5, now(), 'open')
                """,
                ("id", id), ("mid", messageId), ("env", envelope));
        }

        var readOnly = _admin.StaffClient(StaffRoles.ReadOnly);
        var list = await readOnly.GetFromJsonAsync<JsonElement>("/admin/v1/platform/dead-letters?group=media&status=open", Ct);
        list.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("messageId").GetGuid() == messageId).ShouldBe(2);
        (await readOnly.GetAsync($"/admin/v1/platform/dead-letters/{replayId}", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var detail = await _admin.StaffClient(StaffRoles.Support).GetFromJsonAsync<JsonElement>($"/admin/v1/platform/dead-letters/{replayId}", Ct);
        detail.GetProperty("envelope").GetProperty("deadLetter").GetProperty("exceptionMessage").GetString().ShouldBe("boom");

        (await _admin.StaffClient(StaffRoles.SuperAdmin).PostAsJsonAsync("/admin/v1/platform/dead-letters/replay", new { ids = new[] { replayId }, reason = "bug fixed" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var super = _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true);
        var replay = await (await super.PostAsJsonAsync("/admin/v1/platform/dead-letters/replay", new { ids = new[] { replayId, Guid.NewGuid() }, reason = "bug fixed" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        replay.GetProperty("done").GetInt32().ShouldBe(1);
        replay.GetProperty("skipped").GetArrayLength().ShouldBe(1);
        (await (await super.PostAsJsonAsync("/admin/v1/platform/dead-letters/discard", new { ids = new[] { replayId, discardId }, reason = "obsolete" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("done").GetInt32().ShouldBe(1);

        var replayed = await readOnly.GetFromJsonAsync<JsonElement>("/admin/v1/platform/dead-letters?status=replayed&group=media", Ct);
        var row = replayed.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == replayId);
        row.GetProperty("resolutionReason").GetString().ShouldBe("bug fixed");
        var replayMessage = row.GetProperty("replayMessageId").GetGuid();

        // The replay went through the outbox with the failed consumer's name, so no other consumer handles it.
        await using var connection = new NpgsqlConnection(infra.PostgresConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("SELECT envelope->>'replayFor' FROM platform.outbox_messages WHERE id = @id", connection);
        command.Parameters.AddWithValue("id", replayMessage);
        (await command.ExecuteScalarAsync(Ct)).ShouldBe("Some.Handler");
    }

    [Fact]
    public async Task Kafka_lag_and_workers_are_read_from_the_cluster()
    {
        var readOnly = _admin.StaffClient(StaffRoles.ReadOnly);
        var kafka = await readOnly.GetFromJsonAsync<JsonElement>("/admin/v1/platform/kafka", Ct);
        var topic = kafka.GetProperty("topics").EnumerateArray().Single(t => t.GetProperty("name").GetString() == "files.scan-requested.v1");
        topic.GetProperty("exists").GetBoolean().ShouldBeTrue();
        topic.GetProperty("partitions").GetInt32().ShouldBe(3);
        kafka.GetProperty("groups").GetArrayLength().ShouldBe(8);

        var workers = await readOnly.GetFromJsonAsync<JsonElement>("/admin/v1/platform/workers", Ct);
        workers.GetProperty("groups").GetArrayLength().ShouldBe(8);
        (await _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true).PostAsJsonAsync("/admin/v1/platform/jobs/nope/trigger", new { reason = "run it now" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Feature_flags_roll_out_and_a_workspace_override_reaches_the_panel()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var key = "it_flag_" + Guid.NewGuid().ToString("N")[..8];
        var super = _admin.StaffClient(StaffRoles.SuperAdmin);

        (await _admin.StaffClient(StaffRoles.Support).PostAsJsonAsync("/admin/v1/feature-flags", new { key, enabled = true, rolloutPercent = 0, reason = "new inbox beta" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await super.PostAsJsonAsync("/admin/v1/feature-flags", new { key = "Bad Key", enabled = true, rolloutPercent = 101, reason = "new inbox beta" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await super.PostAsJsonAsync("/admin/v1/feature-flags", new { key, description = "صندوق جدید", enabled = true, rolloutPercent = 0, reason = "new inbox beta" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
        (await super.PostAsJsonAsync("/admin/v1/feature-flags", new { key, enabled = true, rolloutPercent = 0, reason = "new inbox beta" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var flags = await _admin.StaffClient(StaffRoles.Support).GetFromJsonAsync<JsonElement>($"/admin/v1/workspaces/{ws}/feature-flags", Ct);
        var flag = flags.EnumerateArray().Single(f => f.GetProperty("key").GetString() == key);
        flag.GetProperty("effective").GetBoolean().ShouldBeFalse();
        flag.GetProperty("source").GetString().ShouldBe("rollout_excluded");

        var overridden = await (await _admin.StaffClient(StaffRoles.Support).PutAsJsonAsync($"/admin/v1/workspaces/{ws}/feature-flags/{key}", new { enabled = true, reason = "beta tester" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        overridden.GetProperty("effective").GetBoolean().ShouldBeTrue();
        overridden.GetProperty("source").GetString().ShouldBe("override");

        // The Api host caches flags for 30 seconds; a fresh host process would see it at once, here we wait for the cache.
        var enabled = false;
        for (var i = 0; i < 40 && !enabled; i++)
        {
            var features = await owner.GetFromJsonAsync<JsonElement>($"/api/v1/workspaces/{ws}/features", Ct);
            enabled = features.GetProperty("enabled").EnumerateArray().Any(e => e.GetString() == key);
            if (!enabled)
            {
                await Task.Delay(1000, Ct);
            }
        }

        enabled.ShouldBeTrue();

        (await super.PostAsJsonAsync($"/admin/v1/feature-flags/{key}/delete", new { reason = "beta finished" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true).PostAsJsonAsync($"/admin/v1/feature-flags/{key}/delete", new { reason = "beta finished" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }
}
