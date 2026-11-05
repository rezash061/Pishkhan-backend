using System.Text.Json;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>Keeps the back office's copy of dead letters in <c>platform.dead_letters</c> (admin plan 4.10, DLQ page).</summary>
public sealed class DeadLetterStore(PlatformDbContext db)
{
    public async Task RecordAsync(string key, EventEnvelope dead, CancellationToken cancellationToken)
    {
        var info = dead.DeadLetter ?? throw new ArgumentException("Not a dead letter.", nameof(dead));
        db.DeadLetters.Add(new DeadLetter
        {
            Id = Uuid.NewV7(info.FailedAt),
            MessageId = dead.MessageId,
            Topic = info.OriginalTopic,
            ConsumerGroup = info.ConsumerGroup,
            Consumer = Cut(info.Consumer, 300),
            Key = Cut(key, 200),
            Type = Cut(dead.Type, 200),
            WorkspaceId = dead.WorkspaceId,
            Envelope = JsonSerializer.SerializeToDocument(dead, EnvelopeJson.Options),
            ExceptionType = Cut(info.ExceptionType, 300),
            ExceptionMessage = Cut(info.ExceptionMessage, 2000),
            Attempts = info.Attempts,
            FailedAt = info.FailedAt,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Puts the message back on its topic through the outbox, with a new message id and <see cref="EventEnvelope.ReplayFor"/> naming the
    /// handler that failed, so only that handler runs it again.
    /// </summary>
    public static OutboxMessage Replay(PlatformDbContext db, DeadLetter letter, DateTimeOffset now)
    {
        var original = letter.Envelope.Deserialize<EventEnvelope>(EnvelopeJson.Options) ?? throw new InvalidOperationException("Unreadable envelope.");
        var replay = original with
        {
            MessageId = Uuid.NewV7(now),
            CausationId = original.MessageId,
            DeadLetter = null,
            ReplayFor = letter.Consumer,
        };

        var message = new OutboxMessage
        {
            Id = replay.MessageId,
            Topic = letter.Topic,
            Key = letter.Key,
            Type = replay.Type,
            Envelope = JsonSerializer.SerializeToDocument(replay, EnvelopeJson.Options),
            OccurredAt = now,
            Shard = OutboxMessage.ShardOf(letter.Key),
            Headers = KafkaProducer.HeadersFor(replay),
        };
        db.OutboxMessages.Add(message);
        letter.Status = DeadLetter.Replayed;
        letter.ReplayMessageId = replay.MessageId;
        return message;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
