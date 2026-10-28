using System.Text.Json;
using System.Text.Json.Serialization;

namespace Pishkhan.Contracts;

/// <summary>
/// An event that crosses module boundaries over Kafka. Implementations are immutable records in a module's
/// <c>*.Contracts</c> project and carry a <see cref="IntegrationEventAttribute"/> naming their topic.
/// </summary>
public interface IIntegrationEvent;

/// <summary>Binds an integration event type to its topic, schema version and partition key.</summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public sealed class IntegrationEventAttribute(string topic, int schemaVersion = 1) : Attribute
{
    public string Topic { get; } = topic;

    public int SchemaVersion { get; } = schemaVersion;

    /// <summary>The event type name on the wire, for example <c>channels.inbound-message-received</c>. Defaults to the topic without its version.</summary>
    public string? Type { get; init; }
}

/// <summary>
/// The wire format of every Kafka message (backend plan section 6.2). The payload is kept as raw JSON so the relay
/// and DLQ tooling never need to know the concrete event type.
/// </summary>
public sealed record EventEnvelope
{
    public required Guid MessageId { get; init; }

    public required string Type { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public Guid? WorkspaceId { get; init; }

    public Guid CorrelationId { get; init; }

    public Guid? CausationId { get; init; }

    public string? Traceparent { get; init; }

    public int SchemaVersion { get; init; } = 1;

    public required JsonElement Payload { get; init; }

    /// <summary>Set only on messages in a DLQ topic.</summary>
    public DeadLetterInfo? DeadLetter { get; init; }
}

public sealed record DeadLetterInfo(string OriginalTopic, string ConsumerGroup, string Consumer, string ExceptionType, string ExceptionMessage, int Attempts, DateTimeOffset FailedAt);

public static class EnvelopeJson
{
    /// <summary>The single JSON configuration for envelopes and payloads: camelCase, unknown fields ignored.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    public static T DeserializePayload<T>(EventEnvelope envelope) =>
        envelope.Payload.Deserialize<T>(Options) ?? throw new JsonException("Empty payload for " + envelope.Type);

    public static JsonElement SerializePayload<T>(T payload) => JsonSerializer.SerializeToElement(payload, Options);
}
