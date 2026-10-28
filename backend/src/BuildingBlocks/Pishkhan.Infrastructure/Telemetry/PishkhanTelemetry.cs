using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Pishkhan.Infrastructure.Telemetry;

/// <summary>Activity sources and meters of the platform (backend plan 3.6).</summary>
public static class PishkhanTelemetry
{
    public const string Name = "Pishkhan";

    public static readonly ActivitySource Messaging = new("Pishkhan.Messaging");
    public static readonly ActivitySource Application = new("Pishkhan.Application");
    public static readonly ActivitySource Ai = new("Pishkhan.AI");

    public static readonly Meter Meter = new(Name);

    public static readonly Counter<long> OutboxRelayed = Meter.CreateCounter<long>("pishkhan.outbox.relayed", description: "Outbox messages produced to Kafka");
    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>("pishkhan.messaging.dead_lettered", description: "Messages sent to a DLQ");
    public static readonly Counter<long> CacheHits = Meter.CreateCounter<long>("pishkhan.cache.hits");
    public static readonly Counter<long> CacheMisses = Meter.CreateCounter<long>("pishkhan.cache.misses");
    public static readonly Counter<long> TimersFired = Meter.CreateCounter<long>("pishkhan.timers.fired");
    public static readonly Histogram<double> AvScanDuration = Meter.CreateHistogram<double>("pishkhan.av.scan.duration", unit: "ms");
    public static readonly Counter<long> AvScanResults = Meter.CreateCounter<long>("pishkhan.av.scan.results");
    public static readonly Counter<long> AiTokens = Meter.CreateCounter<long>("pishkhan.ai.tokens");
    public static readonly Histogram<double> AiLatency = Meter.CreateHistogram<double>("pishkhan.ai.latency", unit: "ms");
    public static readonly Counter<long> InstagramApiErrors = Meter.CreateCounter<long>("pishkhan.instagram.api.errors");

    public static readonly IReadOnlyList<string> ActivitySourceNames = ["Pishkhan.*", "MassTransit", "Npgsql"];
}
