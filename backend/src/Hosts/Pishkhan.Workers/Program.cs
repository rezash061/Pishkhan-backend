using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Jobs;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Agent;
using Pishkhan.Modules.Channels;
using Pishkhan.Modules.Conversations;
using Pishkhan.Modules.Files;
using Pishkhan.Web.Hosting;

// One binary, deployed once per worker group (Workers:Groups), so each group scales on its own (backend plan 2.2).
var builder = WebApplication.CreateBuilder(args);
builder.AddPishkhanWebHost("pishkhan-workers")
    .AddPostgresCheck().AddRedisChecks().AddKafkaCheck();

var services = builder.Services;
var configuration = builder.Configuration;
services.AddAuthentication();
services.AddAuthorization();
services
    .AddFilesModule(configuration)
    .AddChannelsModule(configuration)
    .AddConversationsModule(configuration)
    .AddAgentModule(configuration);

var groups = configuration.GetSection(WorkerGroupOptions.Section).Get<WorkerGroupOptions>() ?? new WorkerGroupOptions();
services.AddPishkhanMessaging(configuration, "workers", m => m
    .AddCacheInvalidation()
    .AddConversationsIngestConsumers()
    .AddConversationsEngineConsumers()
    .AddConversationsOutboundConsumers()
    .AddConversationsPlatformConsumers()
    .AddChannelsPlatformConsumers()
    .AddFilesConsumers());

if (groups.IsEnabled("platform"))
{
    services.AddOutboxRelay(configuration);
    services.AddHostedService<TimerDispatcher>();
    services.AddPishkhanJobs(configuration, [.. QuartzExtensions.PlatformJobs, .. ChannelsModule.Jobs]);
}

var app = builder.Build();
app.UsePishkhanWebHost();
await app.RunAsync();
