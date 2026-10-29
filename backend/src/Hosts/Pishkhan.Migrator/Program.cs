using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Hosting;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Audit.Persistence;
using Pishkhan.Modules.Automation.Persistence;
using Pishkhan.Modules.Catalog.Persistence;
using Pishkhan.Modules.Backoffice;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.Modules.Channels.Persistence;
using Pishkhan.Modules.Conversations.Persistence;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Tenancy.Persistence;
using Pishkhan.Web.Auth;

// Runs once per deploy, before the other hosts (backend plan 12):
//   migrate                                  extensions, EF migrations of every module, Quartz tables, partitions, Kafka topics
//   create-staff <username> <password> <role[,role]>   bootstrap a staff account (first super admin)
var command = args.FirstOrDefault() ?? "migrate";
var builder = Host.CreateApplicationBuilder(args.Skip(1).ToArray());
builder.AddPishkhanPlatform("pishkhan-migrator");
var services = builder.Services;
services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
services.AddModuleDbContext<TenancyDbContext>(TenancyDbContext.SchemaName);
services.AddModuleDbContext<FilesDbContext>(FilesDbContext.SchemaName);
services.AddModuleDbContext<ChannelsDbContext>(ChannelsDbContext.SchemaName);
services.AddModuleDbContext<ConversationsDbContext>(ConversationsDbContext.SchemaName);
services.AddModuleDbContext<AgentDbContext>(AgentDbContext.SchemaName);
services.AddModuleDbContext<AutomationDbContext>(AutomationDbContext.SchemaName);
services.AddModuleDbContext<CatalogDbContext>(CatalogDbContext.SchemaName);
// Staff provisioning reuses the Backoffice handlers, which need the staff token setup.
services.AddRouting();
services.AddPishkhanAuthentication(builder.Configuration, builder.Environment, AuthSchemes.Staff);
services.AddBackofficeModule(builder.Configuration).DecorateHandlers();
services.AddOptions<KafkaOptions>().Bind(builder.Configuration.GetSection(KafkaOptions.Section));
services.AddSingleton<TopicBootstrapper>();

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Migrator");
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
var ct = cts.Token;

switch (command)
{
    case "migrate":
        await MigrateAsync(host.Services, builder.Configuration, logger, ct);
        return 0;
    case "create-staff" when args.Length >= 4:
        await using (var scope = host.Services.CreateAsyncScope())
        {
            var id = await scope.ServiceProvider.GetRequiredService<IStaffProvisioning>()
                .CreateAsync(args[1], args[2], args[3].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), ct);
            logger.LogInformation("Staff {Username} created with id {StaffId}; TOTP enrollment is required at first login", args[1], id);
        }

        return 0;
    default:
        await Console.Error.WriteLineAsync("Usage: migrate | create-staff <username> <password> <role[,role]>");
        return 2;
}

static async Task MigrateAsync(IServiceProvider services, IConfiguration configuration, ILogger logger, CancellationToken ct)
{
    var dataSource = services.GetRequiredService<NpgsqlDataSource>();
    await using (var connection = await dataSource.OpenConnectionAsync(ct))
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE EXTENSION IF NOT EXISTS pg_trgm; CREATE EXTENSION IF NOT EXISTS vector; CREATE SCHEMA IF NOT EXISTS platform;";
        await command.ExecuteNonQueryAsync(ct);
    }

    // Platform first: module contexts map its outbox/inbox/timer tables without owning them.
    Type[] contexts =
    [
        typeof(PlatformDbContext), typeof(IdentityDbContext), typeof(TenancyDbContext), typeof(BackofficeDbContext),
        typeof(FilesDbContext), typeof(ChannelsDbContext), typeof(ConversationsDbContext), typeof(AgentDbContext), typeof(AutomationDbContext), typeof(CatalogDbContext),
    ];
    await using (var scope = services.CreateAsyncScope())
    {
        foreach (var type in contexts)
        {
            var db = (DbContext)scope.ServiceProvider.GetRequiredService(type);
            var pending = (await db.Database.GetPendingMigrationsAsync(ct)).ToList();
            await db.Database.MigrateAsync(ct);
            logger.LogInformation("{Context}: {Count} migration(s) applied", type.Name, pending.Count);
        }
    }

    await using (var connection = await dataSource.OpenConnectionAsync(ct))
    {
        var assembly = Assembly.GetExecutingAssembly();
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".sql", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            await using var stream = assembly.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            await using var command = connection.CreateCommand();
            command.CommandText = await reader.ReadToEndAsync(ct);
            await command.ExecuteNonQueryAsync(ct);
            logger.LogInformation("Applied {Script}", name);
        }

        foreach (var table in Partitioning.PartitionedTables)
        {
            await Partitioning.EnsureMonthlyPartitionsAsync(connection, table, DateTimeOffset.UtcNow.AddMonths(-1), 4, ct);
        }
    }

    if (configuration.GetValue("Migrator:CreateTopics", true))
    {
        await services.GetRequiredService<TopicBootstrapper>().EnsureTopicsAsync(ct);
        logger.LogInformation("Kafka topics ensured");
    }
}
