using System.Collections.Concurrent;
using System.Net;

namespace Pishkhan.IntegrationTests;

/// <summary>The public website's revalidation webhook: records every call the workers make.</summary>
internal sealed class FakeWebsite : HttpMessageHandler
{
    public const string Url = "https://site.test/api/revalidate";
    public const string Secret = "test-revalidate-secret";

    public static readonly ConcurrentQueue<(DateTimeOffset At, string Timestamp, string Signature, string Body)> Calls = new();

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = await request.Content!.ReadAsStringAsync(cancellationToken);
        Calls.Enqueue((DateTimeOffset.UtcNow, request.Headers.GetValues("X-Pishkhan-Timestamp").Single(), request.Headers.GetValues("X-Pishkhan-Signature").Single(), body));
        return new HttpResponseMessage(HttpStatusCode.OK);
    }
}
