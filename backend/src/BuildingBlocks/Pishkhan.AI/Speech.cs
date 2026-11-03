using System.ClientModel;
using System.ClientModel.Primitives;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using OpenAI;
using OpenAI.Audio;

namespace Pishkhan.AI;

/// <summary>A voice note as text. <see cref="Route"/> is the route that transcribed it.</summary>
public sealed record Transcript(string Text, string Route);

/// <summary>
/// Speech to text for <see cref="AiTasks.Transcribe"/> (BE-2.4), over the OpenAI-compatible <c>/audio/transcriptions</c> endpoint that hosted
/// providers and self-hosted Whisper servers expose. Routes are tried in order with the same circuit breakers as chat; when every route
/// fails it throws <see cref="AllRoutesFailedException"/> (and <see cref="NoRouteException"/> when none is configured).
/// </summary>
public interface ISpeechTranscriber
{
    /// <summary><paramref name="audio"/> is read once and buffered, so a second route gets the same bytes.</summary>
    Task<Transcript> TranscribeAsync(Stream audio, string fileName, Guid? workspaceId, CancellationToken cancellationToken);
}

internal sealed class SpeechTranscriber(ModelRouter router, IHttpClientFactory httpClients, ILogger<SpeechTranscriber> logger) : ISpeechTranscriber
{
    /// <summary>The audio language hint: customers of the launch market speak Persian.</summary>
    public const string Language = "fa";

    /// <summary>What the fake provider returns (development and tests).</summary>
    public const string FakeText = "سلام، قیمت این مدل چنده؟";

    public async Task<Transcript> TranscribeAsync(Stream audio, string fileName, Guid? workspaceId, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await audio.CopyToAsync(buffer, cancellationToken);
        Exception? last = null;
        foreach (var route in router.RoutesFor(AiTasks.Transcribe))
        {
            var now = router.Clock.UtcNow;
            if (router.Breakers.IsOpen(route.Provider, now))
            {
                continue;
            }

            if (!router.Options.Providers.TryGetValue(route.Provider, out var provider))
            {
                last = new InvalidOperationException($"AI provider '{route.Provider}' is not configured.");
                continue;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(route.TimeoutSeconds, 60)));
            var started = Stopwatch.GetTimestamp();
            try
            {
                buffer.Position = 0;
                var text = string.Equals(provider.Kind, "fake", StringComparison.OrdinalIgnoreCase)
                    ? FakeText
                    : (await Client(provider, route).TranscribeAudioAsync(buffer, fileName, new AudioTranscriptionOptions { Language = Language }, timeout.Token)).Value.Text;
                router.Breakers.Success(route.Provider);
                Record(route, workspaceId, started, "ok");
                return new Transcript(text?.Trim() ?? string.Empty, route.Key);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                last = ex;
                router.Breakers.Failure(route.Provider, router.Clock.UtcNow, router.Options);
                Record(route, workspaceId, started, ex is OperationCanceledException ? "timeout" : "error");
                logger.LogWarning("AI route {Route} for transcription failed ({Error}); trying the next route", route.Key, ex.GetType().Name);
            }
        }

        throw new AllRoutesFailedException(AiTasks.Transcribe, last ?? new InvalidOperationException("All circuits are open."));
    }

    private AudioClient Client(AiProviderOptions provider, AiRouteOptions route)
    {
        var options = new OpenAIClientOptions
        {
            Transport = new HttpClientPipelineTransport(httpClients.CreateClient(ModelRouter.HttpClientName)),
            RetryPolicy = new ClientRetryPolicy(maxRetries: 0),
        };
        if (provider.BaseUrl is { Length: > 0 } baseUrl)
        {
            options.Endpoint = new Uri(baseUrl);
        }

        return new AudioClient(route.Model, new ApiKeyCredential(provider.ApiKey ?? "unused"), options);
    }

    private void Record(AiRouteOptions route, Guid? workspaceId, long started, string outcome) =>
        router.Usage.Record(new AiUsage(AiTasks.Transcribe, workspaceId, route.Provider, route.Model, 0, 0, 0m, Stopwatch.GetElapsedTime(started), outcome));
}
