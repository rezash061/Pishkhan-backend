using System.Diagnostics;
using System.Text.Json;
using FluentValidation;
using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Telemetry;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Messaging;

public sealed record RetryPolicy(int MaxAttempts, TimeSpan InitialDelay, TimeSpan MaxDelay)
{
    public static readonly RetryPolicy Default = new(5, TimeSpan.FromMilliseconds(200), TimeSpan.FromSeconds(30));

    public TimeSpan DelayFor(int attempt)
    {
        var ms = InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1);
        return TimeSpan.FromMilliseconds(Math.Min(ms, MaxDelay.TotalMilliseconds));
    }
}

/// <summary>Registration data of one handler, available to <see cref="EnvelopeConsumer{TEvent, THandler}"/>.</summary>
public sealed record ConsumerRegistration(Type EventType, Type HandlerType, Type ConsumerType, string Topic, string Group, RetryPolicy Retry, bool Broadcast);

/// <summary>
/// Adapter between a MassTransit Kafka topic endpoint and an <see cref="IIntegrationEventHandler{TEvent}"/>:
/// deserializes the payload, sets the tenant and trace context, retries transient failures with exponential backoff,
/// and sends a message that still fails (or fails permanently) to the group's DLQ topic. It never throws, so one poison
/// message cannot block a partition.
/// </summary>
public sealed class EnvelopeConsumer<TEvent, THandler>(
    IServiceScopeFactory scopes,
    IKafkaProducer producer,
    IEnumerable<ConsumerRegistration> registrations,
    ILogger<EnvelopeConsumer<TEvent, THandler>> logger) : IConsumer<EventEnvelope>
    where TEvent : class, IIntegrationEvent
    where THandler : class, IIntegrationEventHandler<TEvent>
{
    private readonly ConsumerRegistration _registration = registrations.First(r => r.ConsumerType == typeof(EnvelopeConsumer<TEvent, THandler>));

    public async Task Consume(ConsumeContext<EventEnvelope> context)
    {
        var envelope = context.Message;
        var expectedType = Topics.TypeName(typeof(TEvent));
        if (!string.Equals(envelope.Type, expectedType, StringComparison.Ordinal))
        {
            // Several event types may share a topic in future versions; ignore the ones this handler does not know.
            return;
        }

        var attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                await HandleOnceAsync(envelope, context.CancellationToken);
                return;
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsPermanent(ex) || attempt >= _registration.Retry.MaxAttempts)
            {
                logger.LogError(ex, "Message {MessageId} ({Type}) failed in {Consumer} after {Attempts} attempt(s); sending to DLQ", envelope.MessageId, envelope.Type, typeof(THandler).Name, attempt);
                await DeadLetterAsync(context, envelope, ex, attempt);
                return;
            }
            catch (Exception ex)
            {
                var delay = _registration.Retry.DelayFor(attempt);
                logger.LogWarning(ex, "Message {MessageId} failed in {Consumer} (attempt {Attempt}); retrying in {Delay}", envelope.MessageId, typeof(THandler).Name, attempt, delay);
                await Task.Delay(delay, context.CancellationToken);
            }
        }
    }

    private async Task HandleOnceAsync(EventEnvelope envelope, CancellationToken cancellationToken)
    {
        if (envelope.SchemaVersion > Topics.Describe(typeof(TEvent)).SchemaVersion)
        {
            throw new UnsupportedSchemaVersionException(envelope.Type, envelope.SchemaVersion);
        }

        var payload = EnvelopeJson.DeserializePayload<TEvent>(envelope);

        _ = ActivityContext.TryParse(envelope.Traceparent, null, out var parent);
        using var activity = PishkhanTelemetry.Messaging.StartActivity($"consume {_registration.Topic}", ActivityKind.Consumer, parent);
        activity?.SetTag("messaging.system", "kafka");
        activity?.SetTag("messaging.destination.name", _registration.Topic);
        activity?.SetTag("messaging.consumer.group.name", _registration.Group);
        activity?.SetTag("messaging.message.id", envelope.MessageId.ToString());
        activity?.SetTag("pishkhan.handler", typeof(THandler).Name);

        await using var scope = scopes.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ITenantContext>();
        if (envelope.WorkspaceId is { } workspaceId)
        {
            tenant.SetWorkspace(workspaceId);
        }

        using var messaging = MessagingContext.Begin(envelope.MessageId, envelope.CorrelationId);
        var handler = scope.ServiceProvider.GetRequiredService<THandler>();
        await handler.HandleAsync(payload, envelope, cancellationToken);
    }

    private async Task DeadLetterAsync(ConsumeContext<EventEnvelope> context, EventEnvelope envelope, Exception ex, int attempts)
    {
        var key = context.TryGetPayload<KafkaConsumeContext<string>>(out var kafka) ? kafka.Key ?? envelope.MessageId.ToString() : envelope.MessageId.ToString();
        var dead = envelope with
        {
            DeadLetter = new DeadLetterInfo(_registration.Topic, _registration.Group, typeof(THandler).FullName!, ex.GetType().FullName!, Truncate(ex.Message, 2000), attempts, DateTimeOffset.UtcNow),
        };

        try
        {
            await producer.ProduceAsync(Topics.Dlq(_registration.Group), key, dead, CancellationToken.None);
            PishkhanTelemetry.DeadLettered.Add(1, new KeyValuePair<string, object?>("group", _registration.Group), new KeyValuePair<string, object?>("handler", typeof(THandler).Name));
        }
        catch (Exception produceError)
        {
            // If the DLQ itself is down, fail the consume so Kafka redelivers instead of losing the message.
            logger.LogCritical(produceError, "Could not write message {MessageId} to the DLQ", envelope.MessageId);
            throw;
        }
    }

    private static bool IsPermanent(Exception ex) => ex is ValidationException or DomainException or JsonException or UnsupportedSchemaVersionException or CrossTenantWriteException;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

public sealed class UnsupportedSchemaVersionException(string type, int version) : Exception($"Unsupported schema version {version} for {type}.");
