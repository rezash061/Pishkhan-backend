using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Pishkhan.Web.Auth;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// BE-2.9 industry templates: the built-in templates are listed with the workspace's own industry first; applying one adds FAQs and rules
/// switched off, flows published and the agent profile; applying again adds nothing, and what the owner deleted does not come back.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class TemplatesTests(InfraFixture infra, ChannelsHost workers) : IAsyncLifetime
{
    private ApiFactory _api = null!;
    private AdminHost _admin = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _ = workers;
        _api = new ApiFactory(infra);
        _admin = await AdminHost.StartAsync(infra, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _admin.DisposeAsync();
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    [Fact]
    public async Task Applying_a_template_is_idempotent_and_keeps_the_owners_changes()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var root = $"/api/v1/workspaces/{ws}";

        var list = await SendAsync(client, HttpMethod.Get, $"{root}/templates", null, HttpStatusCode.OK);
        list.GetArrayLength().ShouldBe(5);
        var first = list[0];
        first.GetProperty("industry").GetString().ShouldBe("clothing");
        first.GetProperty("recommended").GetBoolean().ShouldBeTrue();
        first.GetProperty("lastApplied").ValueKind.ShouldBe(JsonValueKind.Null);
        var templateId = first.GetProperty("id").GetGuid();

        var preview = await SendAsync(client, HttpMethod.Get, $"{root}/templates/{templateId}", null, HttpStatusCode.OK);
        var faqCount = preview.GetProperty("faqs").GetArrayLength();
        var ruleCount = preview.GetProperty("rules").GetArrayLength();
        var flowCount = preview.GetProperty("flows").GetArrayLength();
        await SendAsync(client, HttpMethod.Get, $"{root}/templates/{Guid.NewGuid()}", null, HttpStatusCode.NotFound);

        var applied = await SendAsync(client, HttpMethod.Post, $"{root}/templates/{templateId}/apply", null, HttpStatusCode.OK);
        applied.GetProperty("faqsAdded").GetInt32().ShouldBe(faqCount);
        applied.GetProperty("rulesAdded").GetInt32().ShouldBe(ruleCount);
        applied.GetProperty("flowsAdded").GetInt32().ShouldBe(flowCount);
        applied.GetProperty("profileApplied").GetBoolean().ShouldBeTrue();
        applied.GetProperty("skipped").GetInt32().ShouldBe(0);

        // Rules are off until the owner switches them on; flows are published so the rules can start them.
        var rules = await SendAsync(client, HttpMethod.Get, $"{root}/automation/rules", null, HttpStatusCode.OK);
        rules.GetArrayLength().ShouldBe(ruleCount);
        rules.EnumerateArray().ShouldAllBe(r => !r.GetProperty("enabled").GetBoolean());
        var flows = await SendAsync(client, HttpMethod.Get, $"{root}/automation/flows", null, HttpStatusCode.OK);
        flows.EnumerateArray().ShouldAllBe(f => f.GetProperty("status").GetString() == "published");

        // The owner deletes a rule and changes the agent's name; applying again adds nothing and touches neither.
        var removed = rules[0].GetProperty("id").GetGuid();
        await SendAsync(client, HttpMethod.Delete, $"{root}/automation/rules/{removed}", null, HttpStatusCode.NoContent);
        var again = await SendAsync(client, HttpMethod.Post, $"{root}/templates/{templateId}/apply", null, HttpStatusCode.OK);
        again.GetProperty("faqsAdded").GetInt32().ShouldBe(0);
        again.GetProperty("rulesAdded").GetInt32().ShouldBe(0);
        again.GetProperty("flowsAdded").GetInt32().ShouldBe(0);
        again.GetProperty("profileApplied").GetBoolean().ShouldBeFalse();
        again.GetProperty("skipped").GetInt32().ShouldBe(faqCount + ruleCount + flowCount);
        (await SendAsync(client, HttpMethod.Get, $"{root}/automation/rules", null, HttpStatusCode.OK)).GetArrayLength().ShouldBe(ruleCount - 1);

        var history = await SendAsync(client, HttpMethod.Get, $"{root}/templates/applications", null, HttpStatusCode.OK);
        history.GetArrayLength().ShouldBe(2);
        (await SendAsync(client, HttpMethod.Get, $"{root}/templates", null, HttpStatusCode.OK))[0].GetProperty("lastApplied").ValueKind.ShouldBe(JsonValueKind.String);
    }

    [Fact]
    public async Task Staff_publish_a_new_version_only_when_it_is_valid()
    {
        var reader = _admin.StaffClient(StaffRoles.ReadOnly);
        var page = await SendAsync(reader, HttpMethod.Get, "/admin/v1/industry-templates?industry=courses", null, HttpStatusCode.OK);
        var published = page.GetProperty("items").EnumerateArray().Single(t => t.GetProperty("status").GetString() == "published");
        published.GetProperty("issues").GetArrayLength().ShouldBe(0);
        var id = published.GetProperty("id").GetGuid();

        await SendAsync(reader, HttpMethod.Post, $"/admin/v1/industry-templates/{id}/versions", null, HttpStatusCode.Forbidden);

        var editor = _admin.StaffClient(StaffRoles.AiOps, stepUp: true);
        var draft = await SendAsync(editor, HttpMethod.Post, $"/admin/v1/industry-templates/{id}/versions", null, HttpStatusCode.Created);
        var draftId = draft.GetProperty("id").GetGuid();
        await SendAsync(editor, HttpMethod.Post, $"/admin/v1/industry-templates/{id}/versions", null, HttpStatusCode.Conflict);

        // A rule naming a flow the template does not have is saved with an issue, and the draft cannot be published.
        var content = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(draft.GetProperty("content").GetRawText())!;
        var rules = JsonSerializer.Deserialize<List<JsonElement>>(content["rules"].GetRawText())!;
        rules.Add(JsonDocument.Parse("""{"key":"broken","name":"خراب","triggerKind":"dm_keyword","trigger":{"keywords":["تست"]},"actions":[{"kind":"start_flow","flowKey":"nope"}],"priority":0}""").RootElement);
        content["rules"] = JsonSerializer.SerializeToElement(rules);
        var saved = await SendAsync(editor, HttpMethod.Put, $"/admin/v1/industry-templates/{draftId}", new { name = "دوره‌های آموزشی ۲", content }, HttpStatusCode.OK);
        saved.GetProperty("issues").EnumerateArray().Select(i => i.GetString()).ShouldContain("rules[broken]: template.flow_key_unknown");
        await SendAsync(editor, HttpMethod.Post, $"/admin/v1/industry-templates/{draftId}/publish", new { }, HttpStatusCode.UnprocessableEntity);

        // Without the broken rule it publishes, and the old version is archived.
        rules.RemoveAt(rules.Count - 1);
        content["rules"] = JsonSerializer.SerializeToElement(rules);
        await SendAsync(editor, HttpMethod.Put, $"/admin/v1/industry-templates/{draftId}", new { name = "دوره‌های آموزشی ۲", content }, HttpStatusCode.OK);
        var live = await SendAsync(editor, HttpMethod.Post, $"/admin/v1/industry-templates/{draftId}/publish", new { reason = "نسخه دوم قالب" }, HttpStatusCode.OK);
        live.GetProperty("status").GetString().ShouldBe("published");
        live.GetProperty("version").GetInt32().ShouldBe(2);
        var old = await SendAsync(reader, HttpMethod.Get, $"/admin/v1/industry-templates/{id}", null, HttpStatusCode.OK);
        old.GetProperty("status").GetString().ShouldBe("archived");
    }
}
