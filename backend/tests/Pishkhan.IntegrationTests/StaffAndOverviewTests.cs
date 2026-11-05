using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>Staff management, AI traces across workspaces, the home dashboard and the platform health page in the AdminApi.</summary>
[Collection(WorkersCollection.Name)]
public sealed class StaffAndOverviewTests(InfraFixture infra) : IAsyncLifetime
{
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await AdminHost.StartAsync(infra, Ct);

    public ValueTask DisposeAsync() => _admin.DisposeAsync();

    private async Task<Guid> CreateStaffAsync(params string[] roles)
    {
        await using var scope = _admin.App.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IStaffProvisioning>()
            .CreateAsync("it-" + Guid.NewGuid().ToString("N")[..10], "Integration-password-1234", roles, Ct);
    }

    private async Task<Guid> SeedTraceAsync(string outcome, double confidence, string guardIssues = "[]")
    {
        var id = Guid.CreateVersion7();
        await using var connection = new NpgsqlConnection(infra.PostgresConnectionString);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO agent.ai_traces(id, workspace_id, conversation_id, message_id, input, intent, facts, tool_calls, guard_issues, reply, confidence, threshold, outcome,
                input_tokens, output_tokens, cost_irr, latency_ms, created_at)
            VALUES (@id, @ws, gen_random_uuid(), gen_random_uuid(), 'قیمت این مانتو چنده؟', 'price', '[]',
                '[{"name":"get_product","arguments":"{\"id\":1}","result":"{\"price\":100}"}]', @guard::jsonb, 'سلام، ۱۰۰ هزار تومان', @c, 0.6, @o,
                100, 20, 12.5, 800, now())
            """,
            connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("ws", Guid.NewGuid());
        command.Parameters.AddWithValue("guard", guardIssues);
        command.Parameters.AddWithValue("c", confidence);
        command.Parameters.AddWithValue("o", outcome);
        await command.ExecuteNonQueryAsync(Ct);
        return id;
    }

    [Fact]
    public async Task SuperAdmin_deactivates_reactivates_resets_totp_and_changes_roles_with_step_up_and_reason()
    {
        var target = await CreateStaffAsync(StaffRoles.Support);
        var super = _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true);

        (await _admin.StaffClient(StaffRoles.SuperAdmin).PostAsJsonAsync($"/admin/v1/staff/{target}/deactivate", new { reason = "left the team" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.Support, stepUp: true).PostAsJsonAsync($"/admin/v1/staff/{target}/deactivate", new { reason = "left the team" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await super.PostAsJsonAsync($"/admin/v1/staff/{target}/deactivate", new { reason = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await super.PostAsJsonAsync($"/admin/v1/staff/{Guid.NewGuid()}/deactivate", new { reason = "left the team" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);

        var deactivated = await (await super.PostAsJsonAsync($"/admin/v1/staff/{target}/deactivate", new { reason = "left the team" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        deactivated.GetProperty("status").GetString().ShouldBe("disabled");
        var activated = await (await super.PostAsJsonAsync($"/admin/v1/staff/{target}/activate", new { reason = "came back" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        activated.GetProperty("status").GetString().ShouldBe("active");
        (await super.PostAsJsonAsync($"/admin/v1/staff/{target}/totp-reset", new { reason = "lost the phone" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var bad = await super.PutAsJsonAsync($"/admin/v1/staff/{target}/roles", new { roles = new[] { "Nope" }, reason = "promote" }, Ct);
        bad.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var roles = await (await super.PutAsJsonAsync($"/admin/v1/staff/{target}/roles", new { roles = new[] { StaffRoles.Support, StaffRoles.Finance }, reason = "now also finance" }, Ct))
            .Content.ReadFromJsonAsync<JsonElement>(Ct);
        roles.GetProperty("roles").EnumerateArray().Select(r => r.GetString()).ShouldBe([StaffRoles.Support, StaffRoles.Finance]);

        var list = await super.GetFromJsonAsync<JsonElement>("/admin/v1/staff", Ct);
        list.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == target).GetProperty("lastActiveAt").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task Traces_are_listed_without_customer_content_and_revealed_with_a_reason()
    {
        var low = await SeedTraceAsync("handed_off", 0.3, """[{"attempt":1,"kind":"price","detail":"قیمت ۹۰ گفته شد"}]""");
        await SeedTraceAsync("sent", 0.9);

        (await _admin.StaffClient(StaffRoles.Finance).GetAsync("/admin/v1/ai/traces", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var reader = _admin.StaffClient(StaffRoles.ReadOnly);
        var list = await reader.GetFromJsonAsync<JsonElement>("/admin/v1/ai/traces?maxConfidence=0.5&guardIssues=true", Ct);
        var row = list.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("id").GetGuid() == low);
        row.TryGetProperty("input", out _).ShouldBeFalse();
        row.GetProperty("guardIssueCount").GetInt32().ShouldBe(1);
        row.GetProperty("toolCallCount").GetInt32().ShouldBe(1);

        var redacted = await reader.GetFromJsonAsync<JsonElement>($"/admin/v1/ai/traces/{low}", Ct);
        redacted.GetProperty("redacted").GetBoolean().ShouldBeTrue();
        redacted.GetProperty("input").ValueKind.ShouldBe(JsonValueKind.Null);
        redacted.GetProperty("toolCalls")[0].GetProperty("arguments").ValueKind.ShouldBe(JsonValueKind.Null);
        redacted.GetProperty("guardIssues")[0].GetProperty("detail").GetString().ShouldBe(string.Empty);

        (await reader.PostAsJsonAsync($"/admin/v1/ai/traces/{low}/reveal", new { reason = "ticket 42 investigation" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        var ops = _admin.StaffClient(StaffRoles.AiOps);
        (await ops.PostAsJsonAsync($"/admin/v1/ai/traces/{low}/reveal", new { reason = "x" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var revealed = await (await ops.PostAsJsonAsync($"/admin/v1/ai/traces/{low}/reveal", new { reason = "ticket 42 investigation" }, Ct)).Content.ReadFromJsonAsync<JsonElement>(Ct);
        revealed.GetProperty("redacted").GetBoolean().ShouldBeFalse();
        revealed.GetProperty("input").GetString().ShouldBe("قیمت این مانتو چنده؟");
        revealed.GetProperty("guardIssues")[0].GetProperty("detail").GetString().ShouldBe("قیمت ۹۰ گفته شد");

        var stats = await reader.GetFromJsonAsync<JsonElement>("/admin/v1/ai/traces/stats", Ct);
        stats.GetProperty("withGuardIssues").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        stats.GetProperty("lowConfidence").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Every_staff_role_reads_the_dashboard_and_the_platform_health()
    {
        await SeedTraceAsync("handed_off", 0.3);
        var reader = _admin.StaffClient(StaffRoles.ContentEditor);

        var dashboard = await reader.GetFromJsonAsync<JsonElement>("/admin/v1/dashboard", Ct);
        var kpis = dashboard.GetProperty("kpis");
        kpis.GetProperty("activeWorkspaces").ValueKind.ShouldBe(JsonValueKind.Number);
        kpis.GetProperty("aiRepliesToday").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
        kpis.GetProperty("mrrRial").ValueKind.ShouldBe(JsonValueKind.Number);
        dashboard.GetProperty("alerts").ValueKind.ShouldBe(JsonValueKind.Array);

        var health = await reader.GetFromJsonAsync<JsonElement>("/admin/v1/platform/health", Ct);
        var checks = health.GetProperty("checks");
        checks.GetProperty("postgres").GetProperty("status").GetString().ShouldBe("Healthy");
        checks.GetProperty("kafka").GetProperty("status").GetString().ShouldNotBe("Unhealthy");
        checks.TryGetProperty("ai", out _).ShouldBeTrue();
        checks.TryGetProperty("instagram", out _).ShouldBeTrue();
    }
}
