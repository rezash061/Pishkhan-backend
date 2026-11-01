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

    /// <summary>
    /// Embeddings of <see cref="AiTasks.Embed"/>, always <see cref="Embeddings.Dimensions"/> long. Only the first route is used: vectors from
    /// different models are not comparable, so a model change means re-indexing, not falling back.
    /// </summary>
    IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(Guid? workspaceId = null);
}

public static class Embeddings
{
    /// <summary>The size of every stored vector (the knowledge index's column type).</summary>
    public const int Dimensions = 1024;
}

/// <summary>Back office: checks that a provider answers, before or after saving it.</summary>
public interface IAiProviderProbe
{
    /// <summary>Sends one tiny request to <paramref name="model"/>. Returns null on success, else the error.</summary>
    Task<string?> TestAsync(AiProviderOptions provider, string model, CancellationToken cancellationToken);
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
    IAiSettings settings,
    IHttpClientFactory httpClients,
    IAiUsageSink usage,
    CircuitBreakers breakers,
    IClock clock,
    ILoggerFactory loggers) : IModelRouter, IAiProviderProbe
{
    public const string HttpClientName = "ai";
    private readonly ConcurrentDictionary<string, (string Fingerprint, IChatClient Client)> _clients = new(StringComparer.Ordinal);

    private readonly ConcurrentDictionary<string, (string Fingerprint, IEmbeddingGenerator<string, Embedding<float>> Generator)> _embedders = new(StringComparer.Ordinal);

    public IChatClient GetChatClient(string task, Guid? workspaceId = null) =>
        new FallbackChatClient(task, workspaceId, this, loggers.CreateLogger<FallbackChatClient>());

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator(Guid? workspaceId = null) => new RoutedEmbeddingGenerator(workspaceId, this);

    internal IEmbeddingGenerator<string, Embedding<float>> EmbedderFor(AiRouteOptions route)
    {
        if (!Options.Providers.TryGetValue(route.Provider, out var provider))
        {
            throw new InvalidOperationException($"AI provider '{route.Provider}' is not configured.");
        }

        var fingerprint = $"{provider.Kind}|{provider.BaseUrl}|{provider.ApiKey?.GetHashCode(StringComparison.Ordinal)}";
        return _embedders.AddOrUpdate(
            route.Key,
            _ => (fingerprint, CreateEmbedder(provider, route)),
            (_, existing) => existing.Fingerprint == fingerprint ? existing : (fingerprint, CreateEmbedder(provider, route))).Generator;
    }

    private IEmbeddingGenerator<string, Embedding<float>> CreateEmbedder(AiProviderOptions provider, AiRouteOptions route)
    {
        if (string.Equals(provider.Kind, "fake", StringComparison.OrdinalIgnoreCase))
        {
            return new FakeEmbeddingGenerator(route.Model);
        }

        var clientOptions = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClients.CreateClient(HttpClientName)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        if (provider.BaseUrl is { Length: > 0 } baseUrl)
        {
            clientOptions.Endpoint = new Uri(baseUrl);
        }

        return new OpenAI.Embeddings.EmbeddingClient(route.Model, new ApiKeyCredential(provider.ApiKey ?? "unused"), clientOptions).AsIEmbeddingGenerator(Embeddings.Dimensions);
    }

    internal IReadOnlyList<AiRouteOptions> RoutesFor(string task) =>
        settings.Current.Routes.TryGetValue(task, out var routes) && routes.Count > 0 ? routes : throw new NoRouteException(task);

    internal AiOptions Options => settings.Current;

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

    public async Task<string?> TestAsync(AiProviderOptions provider, string model, CancellationToken cancellationToken)
    {
        using var client = Create(provider, new AiRouteOptions { Provider = "probe", Model = model });
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "ping")], new ChatOptions { ModelId = model, MaxOutputTokens = 1 }, timeout.Token);
            return null;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            return ex is OperationCanceledException ? "timeout" : ex.GetType().Name + ": " + ex.Message;
        }
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

/// <summary>The first <see cref="AiTasks.Embed"/> route, with its timeout, circuit breaker and usage metering.</summary>
internal sealed class RoutedEmbeddingGenerator(Guid? workspaceId, ModelRouter router) : IEmbeddingGenerator<string, Embedding<float>>
{
    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var route = router.RoutesFor(AiTasks.Embed)[0];
        if (router.Breakers.IsOpen(route.Provider, router.Clock.UtcNow))
        {
            throw new AllRoutesFailedException(AiTasks.Embed, new InvalidOperationException("The embedding provider's circuit is open."));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(route.TimeoutSeconds));
        var started = Stopwatch.GetTimestamp();
        var request = options?.Clone() ?? new EmbeddingGenerationOptions();
        request.ModelId = route.Model;
        request.Dimensions = Embeddings.Dimensions;
        try
        {
            var result = await router.EmbedderFor(route).GenerateAsync(values, request, timeout.Token);
            router.Breakers.Success(route.Provider);
            Record(route, result.Usage?.InputTokenCount ?? 0, Stopwatch.GetElapsedTime(started), "ok");
            return result;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is not NoRouteException)
        {
            router.Breakers.Failure(route.Provider, router.Clock.UtcNow, router.Options);
            Record(route, 0, Stopwatch.GetElapsedTime(started), ex is OperationCanceledException ? "timeout" : "error");
            throw new AllRoutesFailedException(AiTasks.Embed, ex);
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    private void Record(AiRouteOptions route, long input, TimeSpan latency, string outcome)
    {
        var cost = router.Options.Prices.TryGetValue(route.Key, out var price) ? decimal.Round(input * price.InputPerMillion / 1_000_000m, 0) : 0m;
        router.Usage.Record(new AiUsage(AiTasks.Embed, workspaceId, route.Provider, route.Model, input, 0, cost, latency, outcome));
    }
}

/// <summary>
/// Deterministic embeddings for development and tests: each normalised word (and its 3-letter prefix) is hashed into the vector,
/// which is then L2-normalised, so texts sharing words are close by cosine. Model <c>fail</c> always throws.
/// </summary>
internal sealed class FakeEmbeddingGenerator(string model) : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        if (model == "fail")
        {
            throw new HttpRequestException("Fake provider failure.");
        }

        var list = values.ToList();
        var result = new GeneratedEmbeddings<Embedding<float>>(list.Select(v => new Embedding<float>(Vector(v)) { ModelId = model }))
        {
            Usage = new UsageDetails { InputTokenCount = list.Sum(v => (long)Math.Max(1, v.Length / 4)) },
        };
        return Task.FromResult(result);
    }

    public static float[] Vector(string text)
    {
        var vector = new float[Embeddings.Dimensions];
        foreach (var word in Pishkhan.Persian.PersianNormalizer.Normalize(text).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = word.ToLowerInvariant();
            vector[Bucket(token)] += 1f;
            if (token.Length > 3)
            {
                vector[Bucket(token[..3])] += 0.5f;
            }
        }

        var norm = MathF.Sqrt(vector.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] /= norm;
            }
        }
        else
        {
            vector[0] = 1f;
        }

        return vector;
    }

    private static readonly char[] Separators = [' ', '\n', '\t', '.', '،', ',', '؟', '?', '!', '؛', ';', ':', '(', ')', '«', '»', '"', '\u200c'];

    private static int Bucket(string token)
    {
        var hash = 2166136261u;
        foreach (var c in token)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return (int)(hash % Embeddings.Dimensions);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
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
