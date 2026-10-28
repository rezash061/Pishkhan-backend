using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenAI;
using Pishkhan.SharedKernel;

namespace Pishkhan.AI;

/// <summary>Returns the chat client for a task. Callers never name a provider or model.</summary>
public interface IModelRouter
{
    IChatClient GetChatClient(string task, Guid? workspaceId = null);
}

public sealed class NoRouteException(string task) : Exception($"No AI route is configured for task '{task}'.")
{
    public string Task { get; } = task;
}

public sealed class AllRoutesFailedException(string task, Exception inner) : Exception($"Every AI route for task '{task}' failed.", inner)
{
    public string Task { get; } = task;
}

internal sealed class ModelRouter(
    IOptionsMonitor<AiOptions> options,
    IHttpClientFactory httpClients,
    IAiUsageSink usage,
    CircuitBreakers breakers,
    IClock clock,
    ILoggerFactory loggers) : IModelRouter
{
    public const string HttpClientName = "ai";
    private readonly ConcurrentDictionary<string, (string Fingerprint, IChatClient Client)> _clients = new(StringComparer.Ordinal);

    public IChatClient GetChatClient(string task, Guid? workspaceId = null) =>
        new FallbackChatClient(task, workspaceId, this, loggers.CreateLogger<FallbackChatClient>());

    internal IReadOnlyList<AiRouteOptions> RoutesFor(string task) =>
        options.CurrentValue.Routes.TryGetValue(task, out var routes) && routes.Count > 0 ? routes : throw new NoRouteException(task);

    internal AiOptions Options => options.CurrentValue;

    internal IAiUsageSink Usage => usage;

    internal CircuitBreakers Breakers => breakers;

    internal IClock Clock => clock;

    /// <summary>Provider clients are cached per provider/model and rebuilt when the provider's configuration changes.</summary>
    internal IChatClient ClientFor(AiRouteOptions route)
    {
        if (!Options.Providers.TryGetValue(route.Provider, out var provider))
        {
            throw new InvalidOperationException($"AI provider '{route.Provider}' is not configured.");
        }

        var fingerprint = $"{provider.Kind}|{provider.BaseUrl}|{provider.ApiKey?.GetHashCode(StringComparison.Ordinal)}";
        var cached = _clients.AddOrUpdate(
            route.Key,
            _ => (fingerprint, Create(provider, route)),
            (_, existing) => existing.Fingerprint == fingerprint ? existing : (fingerprint, Create(provider, route)));
        return cached.Client;
    }

    private IChatClient Create(AiProviderOptions provider, AiRouteOptions route)
    {
        if (string.Equals(provider.Kind, "fake", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeChatClient(route.Model);
        }

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClients.CreateClient(HttpClientName)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0), // fallback, not retries, handles failures
        };
        if (provider.BaseUrl is { Length: > 0 } baseUrl)
        {
            clientOptions.Endpoint = new Uri(baseUrl);
        }

        return new OpenAI.Chat.ChatClient(route.Model, new ApiKeyCredential(provider.ApiKey ?? "unused"), clientOptions).AsIChatClient();
    }
}

/// <summary>
/// Tries each route of the task in order: skips providers whose circuit is open, applies the route's timeout and parameters,
/// and records usage for every attempt (backend plan 10.1 middleware pipeline).
/// </summary>
internal sealed class FallbackChatClient(string task, Guid? workspaceId, ModelRouter router, ILogger<FallbackChatClient> logger) : IChatClient
{
    private static readonly ActivitySource Source = new("Pishkhan.AI");

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        var list = messages as IList<ChatMessage> ?? messages.ToList();
        Exception? last = null;
        foreach (var route in router.RoutesFor(task))
        {
            if (router.Breakers.IsOpen(route.Provider, router.Clock.UtcNow))
            {
                continue;
            }

            using var activity = Source.StartActivity($"ai {task}", ActivityKind.Client);
            activity?.SetTag("gen_ai.operation.name", "chat");
            activity?.SetTag("gen_ai.request.model", route.Model);
            activity?.SetTag("pishkhan.ai.provider", route.Provider);
            activity?.SetTag("pishkhan.ai.task", task);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(route.TimeoutSeconds));
            var started = Stopwatch.GetTimestamp();
            try
            {
                var response = await router.ClientFor(route).GetResponseAsync(list, Apply(route, options), timeout.Token);
                router.Breakers.Success(route.Provider);
                Record(route, response.Usage, Stopwatch.GetElapsedTime(started), "ok");
                activity?.SetTag("gen_ai.usage.input_tokens", response.Usage?.InputTokenCount);
                activity?.SetTag("gen_ai.usage.output_tokens", response.Usage?.OutputTokenCount);
                response.AdditionalProperties ??= [];
                response.AdditionalProperties["pishkhan.route"] = route.Key;
                return response;
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not NoRouteException)
            {
                last = ex;
                router.Breakers.Failure(route.Provider, router.Clock.UtcNow, router.Options);
                Record(route, null, Stopwatch.GetElapsedTime(started), ex is OperationCanceledException ? "timeout" : "error");
                activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                logger.LogWarning("AI route {Route} for {Task} failed ({Error}); trying the next route", route.Key, task, ex.GetType().Name);
            }
        }

        throw new AllRoutesFailedException(task, last ?? new InvalidOperationException("All circuits are open."));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Streaming is not on any M0 path; buffer through the fallback chain to keep one code path.
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private static ChatOptions Apply(AiRouteOptions route, ChatOptions? options)
    {
        var result = options?.Clone() ?? new ChatOptions();
        result.ModelId = route.Model;
        result.Temperature ??= route.Temperature;
        result.MaxOutputTokens ??= route.MaxOutputTokens;
        return result;
    }

    private void Record(AiRouteOptions route, UsageDetails? details, TimeSpan latency, string outcome)
    {
        var input = details?.InputTokenCount ?? 0;
        var output = details?.OutputTokenCount ?? 0;
        var cost = router.Options.Prices.TryGetValue(route.Key, out var price)
            ? decimal.Round((input * price.InputPerMillion + output * price.OutputPerMillion) / 1_000_000m, 0)
            : 0m;
        router.Usage.Record(new AiUsage(task, workspaceId, route.Provider, route.Model, input, output, cost, latency, outcome));
    }
}

/// <summary>Per-provider circuit breaker: opens after N consecutive failures, half-opens after the configured time.</summary>
internal sealed class CircuitBreakers
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.Ordinal);

    public bool IsOpen(string provider, DateTimeOffset now) =>
        _states.TryGetValue(provider, out var s) && s.OpenUntil > now;

    public void Success(string provider) => _states.TryRemove(provider, out _);

    public void Failure(string provider, DateTimeOffset now, AiOptions options) =>
        _states.AddOrUpdate(
            provider,
            _ => new State(1, DateTimeOffset.MinValue),
            (_, s) => s.Failures + 1 >= options.CircuitBreakerFailures
                ? new State(0, now.AddSeconds(options.CircuitBreakerSeconds))
                : s with { Failures = s.Failures + 1 });

    private sealed record State(int Failures, DateTimeOffset OpenUntil);
}

/// <summary>
/// Deterministic provider for development, tests and dummy eval runs. A system prompt containing <c>json</c> gets
/// <c>{"intent":"other","confidence":0.5}</c>; otherwise the last user message is echoed. Model <c>fail</c> always throws.
/// </summary>
internal sealed class FakeChatClient(string model) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (model == "fail")
        {
            throw new HttpRequestException("Fake provider failure.");
        }

        var list = messages.ToList();
        var wantsJson = list.Any(m => m.Role == ChatRole.System && m.Text.Contains("json", StringComparison.OrdinalIgnoreCase));
        var lastUser = list.LastOrDefault(m => m.Role == ChatRole.User)?.Text ?? string.Empty;
        var text = wantsJson ? """{"intent":"other","confidence":0.5}""" : lastUser;
        var input = list.Sum(m => (long)Math.Max(1, m.Text.Length / 4));
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text))
        {
            ModelId = model,
            Usage = new UsageDetails { InputTokenCount = input, OutputTokenCount = Math.Max(1, text.Length / 4) },
        });
    }

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
