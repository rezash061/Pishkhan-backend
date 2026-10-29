using Confluent.Kafka;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Pishkhan.Contracts;

namespace Pishkhan.Infrastructure.Messaging;

/// <summary>Collects the consumers that modules declare as (topic, handler, group). Hosts decide which groups run.</summary>
public sealed class MessagingBuilder
{
    internal MessagingBuilder(IServiceCollection services, WorkerGroupOptions groups, string hostName)
    {
        Services = services;
        Groups = groups;
        HostName = hostName;
    }

    public IServiceCollection Services { get; }

    public WorkerGroupOptions Groups { get; }

    public string HostName { get; }

    internal List<ConsumerRegistration> Registrations { get; } = [];

    /// <summary>Adds a handler for <typeparamref name="TEvent"/> in a worker group. Ignored when this process does not run the group.</summary>
    public MessagingBuilder AddConsumer<TEvent, THandler>(string group, RetryPolicy? retry = null)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        if (!Groups.IsEnabled(group))
        {
            return this;
        }

        Add<TEvent, THandler>(group, retry, broadcast: false);
        return this;
    }

    /// <summary>
    /// Adds a handler in a group this host type owns whatever <c>Workers:Groups</c> says (for example the Api host's
    /// <c>realtime</c> group, which pushes events to SignalR clients).
    /// </summary>
    public MessagingBuilder AddHostConsumer<TEvent, THandler>(string group)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        Add<TEvent, THandler>(group, null, broadcast: false);
        return this;
    }

    /// <summary>
    /// Adds a handler that every process instance receives (its own consumer group), for example cache invalidation.
    /// Broadcast consumers start from the latest offset.
    /// </summary>
    public MessagingBuilder AddBroadcastConsumer<TEvent, THandler>(string purpose)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        Add<TEvent, THandler>($"{purpose}.{HostName}.{Environment.MachineName}.{Environment.ProcessId}".ToLowerInvariant(), null, broadcast: true);
        return this;
    }

    private void Add<TEvent, THandler>(string group, RetryPolicy? retry, bool broadcast)
        where TEvent : class, IIntegrationEvent
        where THandler : class, IIntegrationEventHandler<TEvent>
    {
        var topic = Topics.Describe(typeof(TEvent)).Topic;
        var consumerType = typeof(EnvelopeConsumer<TEvent, THandler>);
        if (Registrations.Any(r => r.ConsumerType == consumerType))
        {
            return;
        }

        Services.TryAddScoped<THandler>();
        var registration = new ConsumerRegistration(typeof(TEvent), typeof(THandler), consumerType, topic, group, retry ?? RetryPolicy.Default, broadcast);
        Registrations.Add(registration);
        Services.AddSingleton(registration);
    }
}

public static class MessagingExtensions
{
    /// <summary>
    /// Kafka through MassTransit 8.5's rider for consumers (an in-memory bus hosts the rider), and a direct idempotent producer
    /// for the outbox relay and Ingress. Modules only declare (topic, handler, group).
    /// </summary>
    public static IServiceCollection AddPishkhanMessaging(this IServiceCollection services, IConfiguration configuration, string hostName, Action<MessagingBuilder> configure)
    {
        services.AddOptions<KafkaOptions>().Bind(configuration.GetSection(KafkaOptions.Section));
        var groups = configuration.GetSection(WorkerGroupOptions.Section).Get<WorkerGroupOptions>() ?? new WorkerGroupOptions();
        services.AddSingleton(groups);
        services.TryAddSingleton<IKafkaProducer, KafkaProducer>();
        services.TryAddSingleton<TopicBootstrapper>();

        var builder = new MessagingBuilder(services, groups, hostName);
        configure(builder);
        var kafka = configuration.GetSection(KafkaOptions.Section).Get<KafkaOptions>() ?? new KafkaOptions();

        if (kafka.CreateTopicsIfMissing)
        {
            services.AddHostedService<TopicBootstrapHostedService>();
        }

        // No consumers, or the build-time OpenAPI export (which starts the host): no bus to run.
        if (builder.Registrations.Count == 0 || Hosting.BuildTime.IsActive)
        {
            return services;
        }

        services.AddMassTransit(x =>
        {
            x.UsingInMemory((_, _) => { });
            x.AddRider(rider =>
            {
                foreach (var registration in builder.Registrations)
                {
                    rider.AddConsumer(registration.ConsumerType);
                }

                rider.UsingKafka((context, k) =>
                {
                    k.Host(kafka.BootstrapServers, h =>
                    {
                        if (kafka.SaslMechanism is not null)
                        {
                            h.UseSasl(s =>
                            {
                                s.Mechanism = Enum.Parse<SaslMechanism>(kafka.SaslMechanism, ignoreCase: true);
                                s.Username = kafka.User;
                                s.Password = kafka.Password;
                                s.SecurityProtocol = Enum.Parse<SecurityProtocol>(kafka.SecurityProtocol, ignoreCase: true);
                            });
                        }
                    });
                    k.ClientId = $"{kafka.ClientIdPrefix}-{hostName}";

                    foreach (var endpoint in builder.Registrations.GroupBy(r => (r.Topic, r.Group)))
                    {
                        var broadcast = endpoint.Any(r => r.Broadcast);
                        k.TopicEndpoint<string, EventEnvelope>(endpoint.Key.Topic, endpoint.Key.Group, e =>
                        {
                            e.AutoOffsetReset = broadcast ? AutoOffsetReset.Latest : AutoOffsetReset.Earliest;
                            e.ConcurrentMessageLimit = kafka.ConcurrentMessageLimit;
                            e.ConcurrentDeliveryLimit = 1; // per key: keeps a conversation's messages in order
                            e.CheckpointInterval = TimeSpan.FromSeconds(5);
                            e.CheckpointMessageCount = 500;
                            e.SetKeyDeserializer(Deserializers.Utf8);
                            e.SetValueDeserializer(EnvelopeSerializer.Instance);
                            if (kafka.CreateTopicsIfMissing)
                            {
                                var definition = Topics.All.FirstOrDefault(t => t.Name == endpoint.Key.Topic);
                                e.CreateIfMissing(t =>
                                {
                                    t.NumPartitions = (ushort)(kafka.PartitionsOverride ?? definition?.Partitions ?? 6);
                                    t.ReplicationFactor = kafka.ReplicationFactor;
                                });
                            }

                            foreach (var registration in endpoint)
                            {
                                e.ConfigureConsumer(context, registration.ConsumerType);
                            }
                        });
                    }
                });
            });
        });

        return services;
    }

    /// <summary>Registers the outbox relay (platform group) when this process runs the platform group.</summary>
    public static IServiceCollection AddOutboxRelay(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OutboxRelayOptions>().Bind(configuration.GetSection(OutboxRelayOptions.Section));
        services.AddHostedService<OutboxRelayWorker>();
        return services;
    }
}
