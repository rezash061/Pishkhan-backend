using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>BE-1.12 / admin plan 4.5: providers, model routes, prompt versions behind the eval gate, eval sets and runs.</summary>
[Collection(WorkersCollection.Name)]
public sealed class AiAdminTests(InfraFixture infra) : IAsyncLifetime
{
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _admin = await AdminHost.StartAsync(infra, Ct);

    public async ValueTask DisposeAsync() => await _admin.DisposeAsync();

    private async Task<JsonElement> Json(HttpResponseMessage response, HttpStatusCode expected)
    {
        var body = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, body);
        return JsonDocument.Parse(body).RootElement.Clone();
    }

    private async Task<string> ImportAsync(HttpClient ops, string name, string intent)
    {
        var lines = string.Join('\n', Enumerable.Range(1, 3).Select(i =>
            JsonSerializer.Serialize(new { @ref = $"{name}-{i}", vertical = "clothing", input = $"سلام، سوال شماره {i}", expected = new { intent } })));
        var imported = await Json(await ops.PostAsync($"/admin/v1/ai/eval-sets/{name}/import", new StringContent(lines, Encoding.UTF8, "application/x-ndjson"), Ct), HttpStatusCode.OK);
        imported.GetProperty("imported").GetInt32().ShouldBe(3);
        var sets = await ops.GetFromJsonAsync<JsonElement>("/admin/v1/ai/eval-sets", Ct);
        var set = sets.EnumerateArray().First(s => s.GetProperty("name").GetString() == name);
        set.GetProperty("caseCount").GetInt32().ShouldBe(3);
        return set.GetProperty("id").GetString()!;
    }

    private async Task<JsonElement> RunAsync(HttpClient ops, string setId, string version)
    {
        var started = await Json(await ops.PostAsJsonAsync("/admin/v1/ai/eval-runs", new { setId, promptKey = "intent.classify", promptVersion = version }, Ct), HttpStatusCode.Accepted);
        var id = started.GetProperty("id").GetGuid();
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var detail = await ops.GetFromJsonAsync<JsonElement>($"/admin/v1/ai/eval-runs/{id}", Ct);
            if (detail.GetProperty("run").GetProperty("status").GetString() != "running")
            {
                return detail;
            }

            watch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(30), "the eval run finishes");
            await Task.Delay(200, Ct);
        }
    }

    [Fact]
    public async Task Staff_manage_providers_routes_and_prompts_and_activation_needs_a_passing_eval_run()
    {
        var marker = Guid.NewGuid().ToString("N")[..6];
        var provider = "local-" + marker;
        var super = _admin.StaffClient(StaffRoles.SuperAdmin, stepUp: true);
        var ops = _admin.StaffClient(StaffRoles.AiOps);

        // Reading: configured providers; Support has no access.
        var providers = await _admin.StaffClient(StaffRoles.ReadOnly).GetFromJsonAsync<JsonElement>("/admin/v1/ai/providers", Ct);
        providers.EnumerateArray().First(p => p.GetProperty("name").GetString() == "fake").GetProperty("source").GetString().ShouldBe("configuration");
        (await _admin.StaffClient(StaffRoles.Support).GetAsync("/admin/v1/ai/providers", Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // Providers: SuperAdmin with step-up; the key is write-only.
        var body = new { kind = "fake", baseUrl = "https://llm.internal.example/v1", apiKey = "sk-very-secret", clearApiKey = false, enabled = true, reason = "ارائه‌دهنده داخلی" };
        (await ops.PutAsJsonAsync($"/admin/v1/ai/providers/{provider}", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await _admin.StaffClient(StaffRoles.SuperAdmin).PutAsJsonAsync($"/admin/v1/ai/providers/{provider}", body, Ct)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await super.PutAsJsonAsync($"/admin/v1/ai/providers/{provider}", body with { kind = "anthropic-direct" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var saved = await super.PutAsJsonAsync($"/admin/v1/ai/providers/{provider}", body, Ct);
        (await saved.Content.ReadAsStringAsync(Ct)).ShouldNotContain("sk-very-secret");
        var savedJson = await Json(saved, HttpStatusCode.OK);
        savedJson.GetProperty("hasApiKey").GetBoolean().ShouldBeTrue();
        savedJson.GetProperty("source").GetString().ShouldBe("database");
        (await Json(await super.PostAsJsonAsync($"/admin/v1/ai/providers/{provider}/test", new { model = "any" }, Ct), HttpStatusCode.OK)).GetProperty("ok").GetBoolean().ShouldBeTrue();
        (await Json(await super.PostAsJsonAsync($"/admin/v1/ai/providers/{provider}/test", new { model = "fail" }, Ct), HttpStatusCode.OK)).GetProperty("ok").GetBoolean().ShouldBeFalse();

        // Routes: AIOps, over providers in effect.
        var step = new { provider, model = "fake-classifier", temperature = 0.1, maxOutputTokens = 200, timeoutSeconds = 10, inputPricePerMillion = 1000m, outputPricePerMillion = 4000m };
        (await ops.PutAsJsonAsync("/admin/v1/ai/routes/classify", new { routes = new[] { step with { provider = "nope" } }, reason = "مسیر آزمایشی" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ops.PutAsJsonAsync("/admin/v1/ai/routes/unknown_task", new { routes = new[] { step }, reason = "مسیر آزمایشی" }, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var routes = await Json(await ops.PutAsJsonAsync("/admin/v1/ai/routes/classify", new { routes = new[] { step }, reason = "مسیر آزمایشی" }, Ct), HttpStatusCode.OK);
        routes.GetProperty("source").GetString().ShouldBe("database");
        routes.GetProperty("routes")[0].GetProperty("provider").GetString().ShouldBe(provider);
        routes.GetProperty("routes")[0].GetProperty("outputPricePerMillion").GetDecimal().ShouldBe(4000m);

        // A new prompt version is not active until a run of it passes the gate.
        var keys = await ops.GetFromJsonAsync<JsonElement>("/admin/v1/ai/prompts", Ct);
        var before = keys.EnumerateArray().First(k => k.GetProperty("key").GetString() == "intent.classify").GetProperty("activeVersion").GetString()!;
        var created = await Json(await ops.PostAsJsonAsync("/admin/v1/ai/prompts/intent.classify/versions", new { text = "Reply with json only: {\"intent\": ...} " + marker, baseVersion = before }, Ct), HttpStatusCode.Created);
        var version = created.GetProperty("version").GetString()!;
        int.Parse(version, System.Globalization.CultureInfo.InvariantCulture).ShouldBeGreaterThan(int.Parse(before, System.Globalization.CultureInfo.InvariantCulture));
        (await ops.PostAsJsonAsync($"/admin/v1/ai/prompts/intent.classify/versions/{version}/activate", new { evalRunId = Guid.NewGuid(), reason = "فعال‌سازی نسخه" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // The fake model answers «other»: a set expecting another intent fails the gate, one expecting «other» passes.
        var badSet = await ImportAsync(ops, "bad-" + marker, "price_question");
        var goodSet = await ImportAsync(ops, "good-" + marker, "other");
        var failed = await RunAsync(ops, badSet, version);
        failed.GetProperty("run").GetProperty("status").GetString().ShouldBe("completed");
        failed.GetProperty("run").GetProperty("intentAccuracy").GetDecimal().ShouldBe(0m);
        failed.GetProperty("run").GetProperty("route").GetString().ShouldBe($"{provider}/fake-classifier");
        failed.GetProperty("run").GetProperty("gate").GetProperty("passed").GetBoolean().ShouldBeFalse();
        failed.GetProperty("results").GetArrayLength().ShouldBe(3);
        failed.GetProperty("results")[0].GetProperty("correct").GetBoolean().ShouldBeFalse();
        var refused = await ops.PostAsJsonAsync(
            $"/admin/v1/ai/prompts/intent.classify/versions/{version}/activate", new { evalRunId = failed.GetProperty("run").GetProperty("id").GetGuid(), reason = "فعال‌سازی نسخه" }, Ct);
        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await refused.Content.ReadAsStringAsync(Ct)).ShouldContain("ai.prompt.eval_gate");

        var passed = await RunAsync(ops, goodSet, version);
        passed.GetProperty("run").GetProperty("gate").GetProperty("passed").GetBoolean().ShouldBeTrue();
        var passedId = passed.GetProperty("run").GetProperty("id").GetGuid();
        var olderRun = await RunAsync(ops, goodSet, before);
        (await ops.PostAsJsonAsync($"/admin/v1/ai/prompts/intent.classify/versions/{version}/activate", new { evalRunId = olderRun.GetProperty("run").GetProperty("id").GetGuid(), reason = "فعال‌سازی نسخه" }, Ct))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        var activated = await Json(await ops.PostAsJsonAsync($"/admin/v1/ai/prompts/intent.classify/versions/{version}/activate", new { evalRunId = passedId, reason = "فعال‌سازی نسخه" }, Ct), HttpStatusCode.OK);
        activated.GetProperty("activeVersion").GetString().ShouldBe(version);
        activated.GetProperty("activeSource").GetString().ShouldBe("database");

        var versions = await ops.GetFromJsonAsync<JsonElement>("/admin/v1/ai/prompts/intent.classify", Ct);
        var newest = versions[0];
        newest.GetProperty("version").GetString().ShouldBe(version);
        newest.GetProperty("active").GetBoolean().ShouldBeTrue();
        newest.GetProperty("source").GetString().ShouldBe("database");
        newest.GetProperty("baseVersion").GetString().ShouldBe(before);
        newest.GetProperty("latestRun").GetProperty("id").GetGuid().ShouldBe(passedId);
        (await ops.GetFromJsonAsync<JsonElement>($"/admin/v1/ai/eval-runs?promptKey=intent.classify&promptVersion={version}", Ct)).GetProperty("totalCount").GetInt32().ShouldBe(2);

        // An empty chain falls back to configuration.
        var reset = await Json(await ops.PutAsJsonAsync("/admin/v1/ai/routes/classify", new { routes = Array.Empty<object>(), reason = "بازگشت به پیکربندی" }, Ct), HttpStatusCode.OK);
        reset.GetProperty("source").GetString().ShouldBe("configuration");
        reset.GetProperty("routes")[0].GetProperty("provider").GetString().ShouldBe("fake");
    }
}
