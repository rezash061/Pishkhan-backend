using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Testcontainers.Kafka;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

[assembly: AssemblyFixture(typeof(Pishkhan.IntegrationTests.InfraFixture))]

namespace Pishkhan.IntegrationTests;

internal static class TestEnvironment
{
    [ModuleInitializer]
    internal static void Init()
    {
        // The reaper image is not always pullable from CI mirrors; containers are disposed by the fixture instead.
        Environment.SetEnvironmentVariable("TESTCONTAINERS_RYUK_DISABLED", "true");
    }
}

/// <summary>One PostgreSQL, Redis and Kafka for the whole test run, migrated like production by the Migrator's steps.</summary>
public sealed class InfraFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg18").WithDatabase("pishkhan").WithUsername("pishkhan").WithPassword("pishkhan").Build();
    private readonly RedisContainer _redis = new RedisBuilder("redis:8-alpine").Build();
    private readonly KafkaContainer _kafka = new KafkaBuilder("apache/kafka:4.1.0").Build();

    public string StorageRoot { get; } = Path.Combine(Path.GetTempPath(), "pishkhan-it-" + Guid.NewGuid().ToString("N"));

    public string PostgresConnectionString => _postgres.GetConnectionString();

    public const string InstagramAppSecret = "it-app-secret";

    public Dictionary<string, string?> Settings { get; private set; } = [];

    public async ValueTask InitializeAsync()
    {
        await Task.WhenAll(_postgres.StartAsync(), _redis.StartAsync(), _kafka.StartAsync());
        var redis = _redis.GetConnectionString() + ",abortConnect=false";
        Settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = PostgresConnectionString,
            ["ConnectionStrings:RedisCache"] = redis,
            ["ConnectionStrings:RedisState"] = redis,
            ["Redis:KeyPrefix"] = "it:" + Guid.NewGuid().ToString("N")[..8],
            ["Kafka:BootstrapServers"] = _kafka.GetBootstrapAddress(),
            ["Kafka:CreateTopicsIfMissing"] = "true",
            ["Kafka:PartitionsOverride"] = "3",
            ["Kafka:ReplicationFactor"] = "1",
            // Every test signs users and staff in from one address; the production limit (20 an hour per IP) would trip mid-run.
            ["RateLimits:AuthPerHour"] = "1000",
            ["Storage:Provider"] = "filesystem",
            ["Storage:RootPath"] = StorageRoot,
            ["Secrets:ActiveKeyId"] = "it",
            ["Secrets:Keys:it"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Secrets:BlindIndexKey"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["Altcha:Enabled"] = "false",
            ["Sms:Provider"] = "dev",
            ["Workers:Groups:0"] = "*",
            ["Spike:EchoReplies"] = "true",
            ["Instagram:AppId"] = "it-app",
            ["Instagram:AppSecret"] = InstagramAppSecret,
            ["Instagram:WebhookVerifyToken"] = "it-verify",
            ["Instagram:RedirectUri"] = "http://localhost/api/v1/channels/instagram/callback",
            ["Auth:DevKeyDirectory"] = Path.Combine(StorageRoot, "keys"),
            ["Ai:Providers:fake:Kind"] = "fake",
            ["Ai:Routes:classify:0:Provider"] = "fake",
            ["Ai:Routes:classify:0:Model"] = "fake-classifier",
            ["Ai:Prompts:intent.classify:1"] = "Reply with json: {\"intent\": ...}",
            ["Serilog:MinimumLevel:Default"] = "Warning",
        };

        await MigrateAsync();
        await new TopicBootstrapper(Options.Create(new KafkaOptions { BootstrapServers = _kafka.GetBootstrapAddress(), PartitionsOverride = 3, ReplicationFactor = 1 }), NullLogger<TopicBootstrapper>.Instance)
            .EnsureTopicsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await Task.WhenAll(_postgres.DisposeAsync().AsTask(), _redis.DisposeAsync().AsTask(), _kafka.DisposeAsync().AsTask());
        try
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private async Task MigrateAsync()
    {
        await using (var connection = new NpgsqlConnection(PostgresConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS vector;", connection);
            await command.ExecuteNonQueryAsync();
        }

        await Migrate<PlatformDbContext>("platform");
        await Migrate<Modules.Identity.Persistence.IdentityDbContext>("identity");
        await Migrate<Modules.Tenancy.Persistence.TenancyDbContext>("tenancy");
        await Migrate<Modules.Backoffice.Persistence.BackofficeDbContext>("backoffice");
        await Migrate<Modules.Files.Persistence.FilesDbContext>("files");
        await Migrate<Modules.Channels.Persistence.ChannelsDbContext>("channels");
        await Migrate<Modules.Conversations.Persistence.ConversationsDbContext>("conversations");
        await Migrate<Modules.Agent.Persistence.AgentDbContext>("agent");
        await Migrate<Modules.Automation.Persistence.AutomationDbContext>("automation");
        await Migrate<Modules.Content.Persistence.ContentDbContext>("content");
        await Migrate<Modules.Catalog.Persistence.CatalogDbContext>("catalog");
        await Migrate<Modules.Audit.Persistence.AuditDbContext>("audit");

        await using var conn = new NpgsqlConnection(PostgresConnectionString);
        await conn.OpenAsync();
        foreach (var table in Partitioning.PartitionedTables)
        {
            await Partitioning.EnsureMonthlyPartitionsAsync(conn, table, DateTimeOffset.UtcNow.AddMonths(-1), 2);
        }
    }

    private async Task Migrate<TContext>(string schema)
        where TContext : ModuleDbContext
    {
        var options = PersistenceExtensions.DesignTimeOptions<TContext>(schema, PostgresConnectionString);
        await using var db = (TContext)Activator.CreateInstance(typeof(TContext), options, new TenantContext())!;
        await db.Database.MigrateAsync();
    }
}
