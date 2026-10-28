using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Pishkhan.AI;

/// <summary>One model call, successful or not. Recorded for every attempt of a fallback chain.</summary>
public sealed record AiUsage(
    string Task,
    Guid? WorkspaceId,
    string Provider,
    string Model,
    long InputTokens,
    long OutputTokens,
    decimal CostIrr,
    TimeSpan Latency,
    string Outcome);

/// <summary>Where usage goes. The default records metrics and logs; the Billing module adds a sink that emits <c>billing.usage-recorded</c>.</summary>
public interface IAiUsageSink
{
    void Record(AiUsage usage);
}

public sealed class MetricsUsageSink(ILogger<MetricsUsageSink> logger) : IAiUsageSink
{
    public static readonly Meter Meter = new("Pishkhan.AI");
    private static readonly Counter<long> Tokens = Meter.CreateCounter<long>("pishkhan.ai.tokens", "{token}");
    private static readonly Counter<double> Cost = Meter.CreateCounter<double>("pishkhan.ai.cost", "IRR");
    private static readonly Histogram<double> Latency = Meter.CreateHistogram<double>("pishkhan.ai.latency", "ms");

    public void Record(AiUsage usage)
    {
        KeyValuePair<string, object?>[] tags = [new("task", usage.Task), new("provider", usage.Provider), new("model", usage.Model), new("outcome", usage.Outcome)];
        Tokens.Add(usage.InputTokens, [.. tags, new("direction", "input")]);
        Tokens.Add(usage.OutputTokens, [.. tags, new("direction", "output")]);
        Cost.Add((double)usage.CostIrr, tags);
        Latency.Record(usage.Latency.TotalMilliseconds, tags);
        logger.LogInformation(
            "AI {Task} via {Provider}/{Model}: {Outcome} in {LatencyMs} ms, {InputTokens}+{OutputTokens} tokens, {CostIrr} IRR",
            usage.Task, usage.Provider, usage.Model, usage.Outcome, (long)usage.Latency.TotalMilliseconds, usage.InputTokens, usage.OutputTokens, usage.CostIrr);
    }
}
