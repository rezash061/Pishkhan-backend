using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Uuid = Pishkhan.SharedKernel.Uuid;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>
/// Produces envelopes to Kafka. Used by the outbox relay, the DLQ filter and the Ingress host (which has no database).
/// Idempotent producer, acks=all, linger 5 ms (backend plan 3.5). See ADR 0002 for why producing bypasses MassTransit.
/// </summary>
public interface IKafkaProducer
{
    Task ProduceAsync(string topic, string key, EventEnvelope envelope, CancellationToken cancellationToken = default);

    Task ProduceAsync(string topic, string key, ReadOnlyMemory<byte> envelopeJson, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default);
}

public sealed class KafkaProducer : IKafkaProducer, IDisposable
{
    private readonly IProducer<string, byte[]> _producer;

    public KafkaProducer(IOptions<KafkaOptions> options, ILogger<KafkaProducer> logger)
    {
        var o = options.Value;
        var config = new ProducerConfig
        {
            BootstrapServers = o.BootstrapServers,
            ClientId = o.ClientIdPrefix + "-producer",
            EnableIdempotence = true,
            Acks = Acks.All,
            LingerMs = 5,
            MaxInFlight = 5,
            MessageSendMaxRetries = int.MaxValue,
            CompressionType = CompressionType.Lz4,
        };
        KafkaClientConfig.ApplySecurity(config, o);
        _producer = new ProducerBuilder<string, byte[]>(config)
            .SetErrorHandler((_, e) => logger.LogWarning("Kafka producer error {Code}: {Reason}", e.Code, e.Reason))
            .Build();
    }

    public Task ProduceAsync(string topic, string key, EventEnvelope envelope, CancellationToken cancellationToken = default)
    {
        var bytes = EnvelopeSerializer.Instance.Serialize(envelope, SerializationContext.Empty);
        return ProduceAsync(topic, key, bytes, HeadersFor(envelope), cancellationToken);
    }

    public async Task ProduceAsync(string topic, string key, ReadOnlyMemory<byte> envelopeJson, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken = default)
    {
        var message = new Message<string, byte[]> { Key = key, Value = envelopeJson.ToArray(), Headers = [] };
        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                message.Headers.Add(name, Encoding.UTF8.GetBytes(value));
            }
        }

        await _producer.ProduceAsync(topic, message, cancellationToken);
    }

    public static Dictionary<string, string> HeadersFor(EventEnvelope envelope)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["message-id"] = envelope.MessageId.ToString(),
            ["type"] = envelope.Type,
        };
        if (envelope.Traceparent is not null)
        {
            headers["traceparent"] = envelope.Traceparent;
        }

        return headers;
    }

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(5));
        _producer.Dispose();
    }
}

internal static class KafkaClientConfig
{
    public static void ApplySecurity(ClientConfig config, KafkaOptions o)
    {
        config.SecurityProtocol = Enum.Parse<SecurityProtocol>(o.SecurityProtocol, ignoreCase: true);
        if (o.SaslMechanism is not null)
        {
            config.SaslMechanism = Enum.Parse<SaslMechanism>(o.SaslMechanism, ignoreCase: true);
            config.SaslUsername = o.User;
            config.SaslPassword = o.Password;
        }
    }
}

/// <summary>Builds envelopes for an integration event. Used by the outbox and by hosts that produce directly (Ingress).</summary>
public static class EventEnvelopes
{
    public static EventEnvelope Create<TEvent>(TEvent integrationEvent, Guid? workspaceId, Guid? correlationId = null, Guid? causationId = null, DateTimeOffset? occurredAt = null)
        where TEvent : IIntegrationEvent
    {
        var attribute = Topics.Describe(typeof(TEvent));
        return new EventEnvelope
        {
            MessageId = Uuid.NewV7(),
            Type = Topics.TypeName(typeof(TEvent)),
            OccurredAt = occurredAt ?? DateTimeOffset.UtcNow,
            WorkspaceId = workspaceId,
            CorrelationId = correlationId ?? MessagingContext.CurrentCorrelationId ?? Uuid.NewV7(),
            CausationId = causationId ?? MessagingContext.CurrentMessageId,
            Traceparent = Activity.Current?.Id,
            SchemaVersion = attribute.SchemaVersion,
            Payload = EnvelopeJson.SerializePayload(integrationEvent),
        };
    }

    /// <summary>Produces an event straight to Kafka, bypassing the outbox. Only for producers without a database transaction to join (webhook ingress).</summary>
    public static Task PublishAsync<TEvent>(this IKafkaProducer producer, TEvent integrationEvent, string key, Guid? workspaceId = null, CancellationToken cancellationToken = default)
        where TEvent : IIntegrationEvent =>
        producer.ProduceAsync(Topics.Describe(typeof(TEvent)).Topic, key, Create(integrationEvent, workspaceId), cancellationToken);
}
