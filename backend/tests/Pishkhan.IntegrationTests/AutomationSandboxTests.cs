using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Pishkhan.IntegrationTests;

/// <summary>The panel's «test» pane: dry runs of rules and flows, judged by the workspace's business hours, with nothing sent or stored.</summary>
[Collection(WorkersCollection.Name)]
public sealed class AutomationSandboxTests(InfraFixture infra) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra);
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string url, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        var response = await client.SendAsync(request, Ct);
        var content = await response.Content.ReadAsStringAsync(Ct);
        response.StatusCode.ShouldBe(expected, content);
        return content.Length == 0 ? default : JsonDocument.Parse(content).RootElement;
    }

    [Fact]
    public async Task Rules_and_flows_dry_run_with_the_workspace_hours_and_nothing_is_stored()
    {
        var client = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(client, Ct);
        var auto = $"/api/v1/workspaces/{ws}/automation";

        // Open Saturday 09:00-13:00 Tehran only; 2026-10-31 is a Saturday (UTC+03:30).
        await SendAsync(client, HttpMethod.Put, $"/api/v1/workspaces/{ws}/business-hours", new
        {
            days = new[] { new { day = 6, ranges = new[] { new { from = "09:00:00", to = "13:00:00" } } } },
            closedDates = Array.Empty<string>(),
        });
        await SendAsync(client, HttpMethod.Put, $"{auto}/settings", new { aiEnabled = false, fallbackText = "پیامتون رسید", fallbackCooldownMinutes = 60 });

        var flow = await SendAsync(client, HttpMethod.Post, $"{auto}/flows", new
        {
            name = "سایز",
            graph = new
            {
                start = "ask",
                nodes = new object[]
                {
                    new { id = "ask", type = "question", message = new { text = "سایزتون؟" }, answerType = "number", variable = "size", retryText = "فقط عدد بنویسید" },
                    new { id = "wait", type = "delay", delaySeconds = 1800 },
                    new { id = "done", type = "message", message = new { text = "سایز {{flow.size}} برای {{contact.first_name|شما}} موجوده" } },
                    new { id = "tag", type = "action", actions = new[] { new { kind = "add_tag", tag = "sized" } } },
                },
                edges = new[] { new { from = "ask", to = "wait" }, new { from = "wait", to = "done" }, new { from = "done", to = "tag" } },
            },
        }, HttpStatusCode.Created);
        var flowId = flow.GetProperty("id").GetGuid();

        // The draft runs before it is published: a wrong answer gets the retry text, the delay ends at once.
        var run = await SendAsync(client, HttpMethod.Post, $"{auto}/flows/{flowId}/test", new
        {
            answers = new[] { new { text = "بزرگ" }, new { text = "۴۲" } },
            contact = new { name = "سارا احمدی" },
        });
        run.GetProperty("status").GetString().ShouldBe("completed");
        run.GetProperty("variables").GetProperty("size").GetString().ShouldBe("42");
        var turns = run.GetProperty("turns");
        turns.GetArrayLength().ShouldBe(3);
        turns[0].GetProperty("steps")[0].GetProperty("message").GetProperty("text").GetString().ShouldBe("سایزتون؟");
        turns[1].GetProperty("steps")[0].GetProperty("message").GetProperty("text").GetString().ShouldBe("فقط عدد بنویسید");
        var last = turns[2].GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("type").GetString()).ToList();
        last.ShouldBe(["delay", "send_message", "add_tag"]);
        turns[2].GetProperty("steps")[1].GetProperty("message").GetProperty("text").GetString().ShouldBe("سایز 42 برای سارا موجوده");
        await SendAsync(client, HttpMethod.Post, $"{auto}/flows/{flowId}/test", new { published = true }, HttpStatusCode.NotFound);

        await SendAsync(client, HttpMethod.Post, $"{auto}/flows/{flowId}/publish", new { });
        var price = await SendAsync(client, HttpMethod.Post, $"{auto}/rules", new
        {
            name = "قیمت",
            triggerKind = "dm_keyword",
            trigger = new { match = "contains", keywords = new[] { "قیمت" } },
            actions = new object[] { new { kind = "send_message", message = new { text = "سلام {{contact.first_name|دوست عزیز}}" } }, new { kind = "start_flow", flowId } },
            priority = 0,
        }, HttpStatusCode.Created);
        var closed = await SendAsync(client, HttpMethod.Post, $"{auto}/rules", new
        {
            name = "تعطیل",
            triggerKind = "outside_business_hours",
            trigger = new { },
            actions = new[] { new { kind = "send_message", message = new { text = "الان تعطیلیم" } } },
            priority = 0,
        }, HttpStatusCode.Created);

        var matched = await SendAsync(client, HttpMethod.Post, $"{auto}/test", new { text = "قيمت مانتو چنده؟", at = "2026-10-31T06:00:00Z" });
        matched.GetProperty("outcome").GetString().ShouldBe("rule");
        matched.GetProperty("ruleId").GetGuid().ShouldBe(price.GetProperty("id").GetGuid());
        matched.GetProperty("businessOpen").GetBoolean().ShouldBeTrue();
        matched.GetProperty("steps")[0].GetProperty("message").GetProperty("text").GetString().ShouldBe("سلام دوست عزیز");
        matched.GetProperty("flow").GetProperty("status").GetString().ShouldBe("waiting_input");

        // Outside the workspace's hours (Saturday 14:30) a message no rule matches gets the outside-hours rule, then the fallback when open.
        var night = await SendAsync(client, HttpMethod.Post, $"{auto}/test", new { text = "سلام", at = "2026-10-31T11:00:00Z" });
        night.GetProperty("outcome").GetString().ShouldBe("outside_hours");
        night.GetProperty("ruleId").GetGuid().ShouldBe(closed.GetProperty("id").GetGuid());
        var open = await SendAsync(client, HttpMethod.Post, $"{auto}/test", new { text = "سلام", at = "2026-10-31T06:00:00Z" });
        open.GetProperty("outcome").GetString().ShouldBe("fallback");
        open.GetProperty("steps")[0].GetProperty("message").GetProperty("text").GetString().ShouldBe("پیامتون رسید");

        // Nothing was counted: the dry runs left no stats and no flow runs.
        var stats = await client.GetFromJsonAsync<JsonElement>($"{auto}/rules/{price.GetProperty("id").GetGuid()}/stats", Ct);
        stats.GetProperty("fired").GetInt32().ShouldBe(0);

        var settings = await client.GetFromJsonAsync<JsonElement>($"{auto}/settings", Ct);
        settings.TryGetProperty("businessHours", out _).ShouldBeFalse();
        (await SendAsync(client, HttpMethod.Post, $"{auto}/test", new { text = "x", source = "email" }, HttpStatusCode.BadRequest))
            .GetProperty("errors").GetProperty("source")[0].GetString().ShouldBe("automation.test_source_invalid");
    }
}
