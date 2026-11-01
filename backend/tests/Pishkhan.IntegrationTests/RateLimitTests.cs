using System.Net;
using System.Net.Http.Json;

namespace Pishkhan.IntegrationTests;

/// <summary>
/// The tuned API rate limits (BE-1.15), with small limits so the tests reach them. Two hosts: signing in goes through the public write
/// limit from the shared test address, so the small public limits live in a host that never signs in, and the public test calls from its
/// own address (X-Forwarded-For).
/// </summary>
public sealed class RateLimitTests(InfraFixture infra) : IAsyncLifetime
{
    private ApiFactory _api = null!;
    private ApiFactory _site = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync()
    {
        _api = new ApiFactory(infra, new Dictionary<string, string?> { ["RateLimits:WorkspacePerMinute"] = "5" });
        _site = new ApiFactory(infra, new Dictionary<string, string?>
        {
            ["RateLimits:PublicReadsPerMinute"] = "4",
            ["RateLimits:PublicPerMinute"] = "2",
        });
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _api.DisposeAsync();
        await _site.DisposeAsync();
    }

    [Fact]
    public async Task Workspace_limit_counts_members_only_and_answers_429_with_retry_after()
    {
        var owner = await _api.SignInAsync(Ct);
        var ws = await ApiFactory.CreateWorkspaceAsync(owner, Ct);
        var url = $"/api/v1/workspaces/{ws}/quick-replies";

        // An outsider gets 404 and does not use up the workspace's budget.
        var stranger = await _api.SignInAsync(Ct);
        for (var i = 0; i < 8; i++)
        {
            (await stranger.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        }

        for (var i = 0; i < 5; i++)
        {
            (await owner.GetAsync(url, Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"call {i + 1}");
        }

        var limited = await owner.GetAsync(url, Ct);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await limited.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>(Ct)).GetProperty("title").GetString().ShouldBe("common.rate_limited");

        // Calls outside the workspace (the account) are not counted against it.
        (await owner.GetAsync("/api/v1/me", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_reads_and_writes_have_separate_per_ip_limits()
    {
        var client = _site.CreateClient();
        var ip = $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}";
        client.DefaultRequestHeaders.Add("X-Forwarded-For", ip);

        for (var i = 0; i < 4; i++)
        {
            (await client.GetAsync("/api/v1/public/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK, $"read {i + 1}");
        }

        (await client.GetAsync("/api/v1/public/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Writes from the same address have their own, stricter budget.
        for (var i = 0; i < 2; i++)
        {
            (await client.PostAsJsonAsync("/api/v1/public/leads", new { }, Ct)).StatusCode.ShouldNotBe(HttpStatusCode.TooManyRequests, $"write {i + 1}");
        }

        (await client.PostAsJsonAsync("/api/v1/public/leads", new { }, Ct)).StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);

        // Another address is not affected.
        var other = _site.CreateClient();
        other.DefaultRequestHeaders.Add("X-Forwarded-For", $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        (await other.GetAsync("/api/v1/public/plans", Ct)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
