using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using nClam;
using Pishkhan.Infrastructure.Telemetry;

namespace Pishkhan.Infrastructure.Antivirus;

public enum ScanVerdict
{
    Clean,
    Infected,
    Error,
}

public sealed record ScanResult(ScanVerdict Verdict, string? Signature, string EngineVersion, string? Error = null);

public interface IAntivirusScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default);

    /// <summary>Engine and signature version, for example <c>ClamAV 1.4.3/27800/Tue Oct 27 08:00:00 2026</c>.</summary>
    Task<string?> GetVersionAsync(CancellationToken cancellationToken = default);

    Task<bool> PingAsync(CancellationToken cancellationToken = default);
}

public sealed class ClamAvOptions
{
    public const string Section = "ClamAv";

    public string Host { get; set; } = "localhost";

    public int Port { get; set; } = 3310;

    /// <summary>Must not exceed clamd's <c>StreamMaxLength</c>.</summary>
    public long MaxStreamSize { get; set; } = 110 * 1024 * 1024;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// clamd over TCP with INSTREAM (backend plan 8.1). Anything other than a clean result is not clean: limits exceeded
/// (clamd reports them as findings with <c>AlertExceedsMax yes</c>) count as infected, transport errors as errors (fail closed).
/// </summary>
public sealed class ClamAvScanner(IOptions<ClamAvOptions> options) : IAntivirusScanner
{
    private ClamClient Client()
    {
        var o = options.Value;
        return new ClamClient(o.Host, o.Port) { MaxStreamSize = o.MaxStreamSize };
    }

    public async Task<ScanResult> ScanAsync(Stream content, CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.GetTimestamp();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Value.Timeout);
        string version = "unknown";
        try
        {
            var client = Client();
            version = (await client.GetVersionAsync(timeout.Token)) ?? version;
            var result = await client.SendAndScanFileAsync(content, timeout.Token);
            var scan = result.Result switch
            {
                ClamScanResults.Clean => new ScanResult(ScanVerdict.Clean, null, version),
                ClamScanResults.VirusDetected => new ScanResult(ScanVerdict.Infected, result.InfectedFiles?.FirstOrDefault()?.VirusName ?? "unknown", version),
                _ => new ScanResult(ScanVerdict.Error, null, version, result.RawResult),
            };
            Record(scan, started);
            return scan;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var scan = new ScanResult(ScanVerdict.Error, null, version, ex.GetType().Name + ": " + ex.Message);
            Record(scan, started);
            return scan;
        }
    }

    public async Task<string?> GetVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Client().GetVersionAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException)
        {
            return null;
        }
    }

    public async Task<bool> PingAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await Client().PingAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or TimeoutException)
        {
            return false;
        }
    }

    private static void Record(ScanResult scan, long started)
    {
        PishkhanTelemetry.AvScanDuration.Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        PishkhanTelemetry.AvScanResults.Add(1, new KeyValuePair<string, object?>("verdict", scan.Verdict.ToString()));
    }
}

public static class AntivirusExtensions
{
    public static IServiceCollection AddPishkhanAntivirus(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ClamAvOptions>().Bind(configuration.GetSection(ClamAvOptions.Section));
        services.AddSingleton<IAntivirusScanner, ClamAvScanner>();
        return services;
    }
}
