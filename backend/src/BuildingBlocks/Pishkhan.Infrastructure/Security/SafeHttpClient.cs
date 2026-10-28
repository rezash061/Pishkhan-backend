using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;

namespace Pishkhan.Infrastructure.Security;

/// <summary>
/// SSRF protection for every server-side fetch of a URL that came from outside (backend plan 3.4): HTTPS only, the resolved
/// address must be public (checked after DNS resolution, at connect time, so DNS rebinding cannot bypass it), optional host
/// allowlist, no automatic redirects, and size and time limits.
/// </summary>
public sealed class SafeHttpClient(IHttpClientFactory factory)
{
    public const string ClientName = "safe";

    public static readonly IReadOnlyList<string> MetaCdnSuffixes = [".cdninstagram.com", ".fbcdn.net", ".fbsbx.com"];

    public async Task<SafeDownload> DownloadAsync(Uri url, long maxBytes, IReadOnlyList<string>? allowedHostSuffixes = null, int maxRedirects = 3, CancellationToken cancellationToken = default)
    {
        var client = factory.CreateClient(ClientName);
        var current = url;
        for (var hop = 0; hop <= maxRedirects; hop++)
        {
            EnsureAllowed(current, allowedHostSuffixes);
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
                response.Dispose();
                continue;
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maxBytes)
            {
                response.Dispose();
                throw new SafeHttpException("response.too_large");
            }

            var buffer = new MemoryStream();
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
            {
                var chunk = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
                {
                    if (buffer.Length + read > maxBytes)
                    {
                        throw new SafeHttpException("response.too_large");
                    }

                    buffer.Write(chunk, 0, read);
                }
            }

            buffer.Position = 0;
            var contentType = response.Content.Headers.ContentType?.MediaType;
            response.Dispose();
            return new SafeDownload(buffer, contentType, current);
        }

        throw new SafeHttpException("response.too_many_redirects");
    }

    public static void EnsureAllowed(Uri url, IReadOnlyList<string>? allowedHostSuffixes)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
        {
            throw new SafeHttpException("url.https_required");
        }

        if (!url.IsDefaultPort)
        {
            throw new SafeHttpException("url.port_not_allowed");
        }

        if (allowedHostSuffixes is { Count: > 0 } && !allowedHostSuffixes.Any(s => url.Host.EndsWith(s, StringComparison.OrdinalIgnoreCase) || string.Equals("." + url.Host, s, StringComparison.OrdinalIgnoreCase)))
        {
            throw new SafeHttpException("url.host_not_allowed");
        }

        if (IPAddress.TryParse(url.Host, out var literal) && !IsPublic(literal))
        {
            throw new SafeHttpException("url.address_not_public");
        }
    }

    /// <summary>False for loopback, private, link-local, CGNAT, multicast, reserved and unique-local addresses.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 10
                || b[0] == 0
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 192 && b[1] == 0 && b[2] == 0)
                || (b[0] == 198 && (b[1] == 18 || b[1] == 19))
                || b[0] >= 224);
        }

        return !(address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal);
    }

    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(10),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = async (context, cancellationToken) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
            var target = addresses.FirstOrDefault(IsPublic) ?? throw new SafeHttpException("url.address_not_public");
            if (addresses.Any(a => !IsPublic(a)))
            {
                // A name that resolves to any private address is refused outright.
                throw new SafeHttpException("url.address_not_public");
            }

            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(target, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

public sealed record SafeDownload(MemoryStream Content, string? ContentType, Uri FinalUrl);

public sealed class SafeHttpException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public static class SafeHttpExtensions
{
    public static IServiceCollection AddSafeHttpClient(this IServiceCollection services)
    {
        services.AddHttpClient(SafeHttpClient.ClientName, c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(SafeHttpClient.CreateHandler);
        services.AddSingleton<SafeHttpClient>();
        return services;
    }
}
