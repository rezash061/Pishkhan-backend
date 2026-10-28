using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.AI;

namespace Pishkhan.UnitTests;

public class AiGatewayTests
{
    private sealed class RecordingSink : IAiUsageSink
    {
        public List<AiUsage> Usages { get; } = [];

        public void Record(AiUsage usage) => Usages.Add(usage);
    }

    private static (ServiceProvider Services, IConfigurationRoot Config, RecordingSink Sink) Build(Dictionary<string, string?> settings)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var sink = new RecordingSink();
        var services = new ServiceCollection().AddLogging().AddSingleton<IAiUsageSink>(sink).AddPishkhanAi(config).BuildServiceProvider();
        return (services, config, sink);
    }

    private static readonly Dictionary<string, string?> TwoRoutes = new()
    {
        ["Ai:Providers:fake:Kind"] = "fake",
        ["Ai:Routes:classify:0:Provider"] = "fake",
        ["Ai:Routes:classify:0:Model"] = "fail",
        ["Ai:Routes:classify:1:Provider"] = "fake",
        ["Ai:Routes:classify:1:Model"] = "backup",
        ["Ai:Prices:fake/backup:InputPerMillion"] = "1000000",
        ["Ai:Prices:fake/backup:OutputPerMillion"] = "2000000",
    };

    [Fact]
    public async Task Falls_back_to_the_next_route_and_meters_every_attempt()
    {
        var (services, _, sink) = Build(TwoRoutes);
        var client = services.GetRequiredService<IModelRouter>().GetChatClient(AiTasks.Classify, Guid.NewGuid());

        var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "سلام")], cancellationToken: TestContext.Current.CancellationToken);

        response.Text.ShouldBe("سلام");
        response.AdditionalProperties!["pishkhan.route"].ShouldBe("fake/backup");
        sink.Usages.Select(u => (u.Model, u.Outcome)).ShouldBe([("fail", "error"), ("backup", "ok")]);
        sink.Usages[1].CostIrr.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task Route_change_in_configuration_switches_model_without_restart()
    {
        var (services, config, sink) = Build(new Dictionary<string, string?>
        {
            ["Ai:Providers:fake:Kind"] = "fake",
            ["Ai:Routes:classify:0:Provider"] = "fake",
            ["Ai:Routes:classify:0:Model"] = "model-a",
        });
        var router = services.GetRequiredService<IModelRouter>();
        var ct = TestContext.Current.CancellationToken;

        await router.GetChatClient(AiTasks.Classify).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: ct);
        config["Ai:Routes:classify:0:Model"] = "model-b";
        config.Reload();
        await router.GetChatClient(AiTasks.Classify).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: ct);

        sink.Usages.Select(u => u.Model).ShouldBe(["model-a", "model-b"]);
    }

    [Fact]
    public async Task Circuit_opens_after_repeated_failures_and_skips_the_provider()
    {
        var settings = new Dictionary<string, string?>(TwoRoutes)
        {
            ["Ai:Providers:broken:Kind"] = "fake",
            ["Ai:Routes:classify:0:Provider"] = "broken",
            ["Ai:CircuitBreakerFailures"] = "2",
        };
        var (services, _, sink) = Build(settings);
        var client = services.GetRequiredService<IModelRouter>().GetChatClient(AiTasks.Classify);
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 4; i++)
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: ct);
        }

        sink.Usages.Count(u => u.Provider == "broken").ShouldBe(2);
        sink.Usages.Count(u => u.Model == "backup").ShouldBe(4);
    }

    [Fact]
    public async Task Missing_route_and_all_routes_failing_are_reported()
    {
        var (services, _, _) = Build(new Dictionary<string, string?>
        {
            ["Ai:Providers:fake:Kind"] = "fake",
            ["Ai:Routes:classify:0:Provider"] = "fake",
            ["Ai:Routes:classify:0:Model"] = "fail",
        });
        var router = services.GetRequiredService<IModelRouter>();
        var ct = TestContext.Current.CancellationToken;

        await Should.ThrowAsync<NoRouteException>(() => router.GetChatClient(AiTasks.Summarize).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: ct));
        await Should.ThrowAsync<AllRoutesFailedException>(() => router.GetChatClient(AiTasks.Classify).GetResponseAsync([new ChatMessage(ChatRole.User, "x")], cancellationToken: ct));
    }

    [Fact]
    public void Prompt_registry_uses_the_active_version_and_checks_placeholders()
    {
        var (services, _, _) = Build(new Dictionary<string, string?>
        {
            ["Ai:Prompts:greet:1"] = "سلام {{name}}",
            ["Ai:Prompts:greet:2"] = "درود {{ name }}",
            ["Ai:ActivePrompts:greet"] = "1",
        });
        var prompts = services.GetRequiredService<IPromptRegistry>();

        prompts.Get("greet").Render(new Dictionary<string, string> { ["name"] = "مریم" }).ShouldBe("سلام مریم");
        prompts.Get("greet", "2").Render(new Dictionary<string, string> { ["name"] = "مریم" }).ShouldBe("درود مریم");
        Should.Throw<InvalidOperationException>(() => prompts.Get("greet").Render(new Dictionary<string, string>()));
        Should.Throw<KeyNotFoundException>(() => prompts.Get("missing"));
    }
}
