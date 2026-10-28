namespace Pishkhan.Infrastructure.Messaging;

public sealed class KafkaOptions
{
    public const string Section = "Kafka";

    public string BootstrapServers { get; set; } = "localhost:9092";

    /// <summary>Plaintext, Ssl, SaslPlaintext or SaslSsl. Production uses SaslSsl (backend plan 3.4).</summary>
    public string SecurityProtocol { get; set; } = "Plaintext";

    public string? SaslMechanism { get; set; }

    public string? User { get; set; }

    public string? Password { get; set; }

    public string ClientIdPrefix { get; set; } = "pishkhan";

    /// <summary>Dev and tests only: create topics on startup. Production topics are created by the Migrator.</summary>
    public bool CreateTopicsIfMissing { get; set; }

    public short ReplicationFactor { get; set; } = 1;

    /// <summary>Concurrent messages per topic endpoint across keys. Messages with the same key are always sequential.</summary>
    public int ConcurrentMessageLimit { get; set; } = 64;

    /// <summary>Overrides the partition count of every topic (tests use 3 to keep brokers light).</summary>
    public int? PartitionsOverride { get; set; }
}

/// <summary>Which worker groups this process runs (backend plan 2.1). Empty means none; "*" means all.</summary>
public sealed class WorkerGroupOptions
{
    public const string Section = "Workers";

    public List<string> Groups { get; set; } = [];

    public bool IsEnabled(string group) => Groups.Contains("*") || Groups.Contains(group, StringComparer.OrdinalIgnoreCase);
}
