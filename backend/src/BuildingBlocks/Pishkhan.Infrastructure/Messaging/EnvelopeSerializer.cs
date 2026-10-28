using System.Text.Json;
using Confluent.Kafka;
using Pishkhan.Contracts;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>Kafka value (de)serializer for <see cref="EventEnvelope"/>: plain UTF-8 JSON, no MassTransit wrapper, so any tool can read the topics.</summary>
public sealed class EnvelopeSerializer : ISerializer<EventEnvelope>, IDeserializer<EventEnvelope>
{
    public static readonly EnvelopeSerializer Instance = new();

    public byte[] Serialize(EventEnvelope data, SerializationContext context) => JsonSerializer.SerializeToUtf8Bytes(data, EnvelopeJson.Options);

    public EventEnvelope Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
    {
        if (isNull)
        {
            throw new JsonException("Null Kafka message value.");
        }

        return JsonSerializer.Deserialize<EventEnvelope>(data, EnvelopeJson.Options) ?? throw new JsonException("Empty envelope.");
    }
}
