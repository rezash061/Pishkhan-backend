using System.Collections.Concurrent;
using System.Text.Json;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Hosting;
using Pishkhan.Infrastructure.Locks;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.RateLimiting;

namespace Pishkhan.IntegrationTests;

[IntegrationEvent("it.probe.v1")]
public sealed record ProbeEvent(string Run, int Seq, bool Fail) : IIntegrationEvent;

public sealed class ProbeLog
{
    public ConcurrentQueue<(string Run, int Seq)> Seen { get; } = new();

    public ConcurrentDictionary<string, int> Attempts { get; } = new();
}

internal sealed class ProbeHandler(PlatformDbContext db, ProbeLog log) : IdempotentHandler<ProbeEvent, PlatformDbContext>(db)
{
    protected override Task HandleOnceAsync(ProbeEvent message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        log.Attempts.AddOrUpdate(message.Run, 1, (_, n) => n + 1);
        if (message.Fail)
        {
            throw new InvalidOperationException("probe failure");
        }

        log.Seen.Enqueue((message.Run, message.Seq));
        return Task.CompletedTask;
    }
}

/// <summary>One host with the probe consumer and the outbox relay for all messaging tests (a consumer group rebalance per test would be slow).</summary>
public sealed class MessagingHost(InfraFixture infra) : IAsyncLifetime
{
    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(infra.Settings);
        builder.Configuration["Outbox:IdleDelay"] = "00:00:00.100";
        builder.AddPishkhanPlatform("it-messaging");
        builder.Services.AddSingleton<ProbeLog>();
        builder.Services.AddPishkhanMessaging(builder.Configuration, "it", m => m
            .AddConsumer<ProbeEvent, ProbeHandler>("engine", new RetryPolicy(3, TimeSpan.FromMilliseconds(20), TimeSpan.FromMilliseconds(50))));
        builder.Services.AddOutboxRelay(builder.Configuration);
        Host = builder.Build();
        await Host.StartAsync();

        // Wait until the consumer owns its partitions: a warm-up message must come back.
        var warmup = Guid.NewGuid().ToString("N");
        await using (var scope = Host.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
            db.Publish(new ProbeEvent(warmup, 0, false), "warmup");
            await db.SaveChangesAsync();
        }

        var log = Host.Services.GetRequiredService<ProbeLog>();
        var until = DateTime.UtcNow.AddSeconds(90);
        while (!log.Seen.Any(s => s.Run == warmup))
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("The probe consumer did not start.");
            }

            await Task.Delay(200);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Host.StopAsync();
        Host.Dispose();
    }
}

/// <summary>Outbox → Kafka → consumer, inbox dedupe, per-key order, DLQ; Redis lock and token bucket (backend plan 6 and 4).</summary>
public sealed class MessagingTests(InfraFixture infra, MessagingHost messaging) : IClassFixture<MessagingHost>
{
    private IHost _host => messaging.Host;

    private ProbeLog Log => _host.Services.GetRequiredService<ProbeLog>();

    private async Task PublishAsync(string key, params ProbeEvent[] events)
    {
        await using var scope = _host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        foreach (var e in events)
        {
            db.Publish(e, key);
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task Eventually(Func<bool> condition, int seconds = 60)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            if (DateTime.UtcNow > until)
            {
                throw new TimeoutException("Condition not met in time.");
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task Outbox_events_reach_the_consumer_once_and_in_key_order()
    {
        var run = Guid.NewGuid().ToString("N");
        await PublishAsync("conversation-1", Enumerable.Range(1, 30).Select(i => new ProbeEvent(run, i, false)).ToArray());

        await Eventually(() => Log.Seen.Count(s => s.Run == run) >= 30);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        Log.Seen.Where(s => s.Run == run).Select(s => s.Seq).ShouldBe(Enumerable.Range(1, 30));
    }

    [Fact]
    public async Task Redelivered_message_is_handled_once()
    {
        var run = Guid.NewGuid().ToString("N");
        var envelope = EventEnvelopes.Create(new ProbeEvent(run, 1, false), null);
        var producer = _host.Services.GetRequiredService<IKafkaProducer>();
        await producer.ProduceAsync("it.probe.v1", "dup", envelope, TestContext.Current.CancellationToken);
        await producer.ProduceAsync("it.probe.v1", "dup", envelope, TestContext.Current.CancellationToken);

        await Eventually(() => Log.Seen.Any(s => s.Run == run));
        await Task.Delay(1500, TestContext.Current.CancellationToken);

        Log.Seen.Count(s => s.Run == run).ShouldBe(1);
    }

    [Fact]
    public async Task Failing_message_is_retried_then_sent_to_the_group_dlq()
    {
        var run = Guid.NewGuid().ToString("N");
        using var consumer = new ConsumerBuilder<string, string>(new ConsumerConfig
        {
            BootstrapServers = infra.Settings["Kafka:BootstrapServers"],
            GroupId = "it-dlq-" + run,
            AutoOffsetReset = AutoOffsetReset.Earliest,
        }).Build();
        consumer.Subscribe(Topics.Dlq("engine"));

        await PublishAsync("poison", new ProbeEvent(run, 1, true));

        EventEnvelope? dead = null;
        var until = DateTime.UtcNow.AddSeconds(60);
        while (dead is null && DateTime.UtcNow < until)
        {
            var result = consumer.Consume(TimeSpan.FromMilliseconds(500));
            if (result?.Message.Value is { } value && value.Contains(run, StringComparison.Ordinal))
            {
                dead = JsonSerializer.Deserialize<EventEnvelope>(value, EnvelopeJson.Options);
            }
        }

        dead.ShouldNotBeNull();
        dead.DeadLetter.ShouldNotBeNull();
        dead.DeadLetter.OriginalTopic.ShouldBe("it.probe.v1");
        dead.DeadLetter.Attempts.ShouldBe(3);
        Log.Attempts[run].ShouldBe(3);
    }

    [Fact]
    public async Task Distributed_lock_is_exclusive_until_released()
    {
        var locks = _host.Services.GetRequiredService<IDistributedLock>();
        var resource = "it-lock-" + Guid.NewGuid().ToString("N");
        var ct = TestContext.Current.CancellationToken;

        await using (var first = await locks.TryAcquireAsync(resource, TimeSpan.FromSeconds(10), cancellationToken: ct))
        {
            first.ShouldNotBeNull();
            (await locks.TryAcquireAsync(resource, TimeSpan.FromSeconds(10), cancellationToken: ct)).ShouldBeNull();
        }

        await using var again = await locks.TryAcquireAsync(resource, TimeSpan.FromSeconds(10), cancellationToken: ct);
        again.ShouldNotBeNull();
    }

    [Fact]
    public async Task Token_bucket_allows_capacity_then_limits()
    {
        var buckets = _host.Services.GetRequiredService<RedisTokenBucket>();
        var bucket = "it-bucket-" + Guid.NewGuid().ToString("N");
        var results = new List<bool>();
        for (var i = 0; i < 7; i++)
        {
            results.Add((await buckets.TryTakeAsync(bucket, capacity: 5, refillPerSecond: 0.01)).Allowed);
        }

        results.ShouldBe([true, true, true, true, true, false, false]);
    }
}
