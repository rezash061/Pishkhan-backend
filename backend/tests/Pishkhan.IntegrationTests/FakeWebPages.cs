using System.Collections.Concurrent;
using System.Net;
using System.Text;

namespace Pishkhan.IntegrationTests;

/// <summary>Public web pages behind the SSRF-safe client (named client <c>safe</c>): tests put pages in <see cref="Pages"/> by URL.</summary>
internal sealed class FakeWebPages : HttpMessageHandler
{
    public static readonly ConcurrentDictionary<string, (string ContentType, byte[] Body)> Pages = new(StringComparer.Ordinal);

    public static void Html(string url, string html) => Pages[url] = ("text/html", Encoding.UTF8.GetBytes(html));

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!Pages.TryGetValue(request.RequestUri!.AbsoluteUri, out var page))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var content = new ByteArrayContent(page.Body);
        content.Headers.TryAddWithoutValidation("Content-Type", page.ContentType + "; charset=utf-8");
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
    }
}
