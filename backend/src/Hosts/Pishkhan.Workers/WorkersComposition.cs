using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Jobs;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Timers;
using Pishkhan.Modules.Agent;
using Pishkhan.Modules.Analytics;
using Pishkhan.Modules.Audit;
using Pishkhan.Modules.Automation;
using Pishkhan.Modules.Billing;
using Pishkhan.Modules.Knowledge;
using Pishkhan.Modules.Catalog;
using Pishkhan.Modules.Orders;
using Pishkhan.Modules.Payments;
using Pishkhan.Modules.Channels;
using Pishkhan.Modules.Content;
using Pishkhan.Modules.Conversations;
using Pishkhan.Modules.Identity;
using Pishkhan.Modules.Notifications;
using Pishkhan.Modules.Tenancy;
using Pishkhan.Modules.Files;
using Pishkhan.Web.Hosting;

namespace Pishkhan.Workers;

/// <summary>
/// The Workers composition, kept out of Program.cs so a test can build it with ValidateOnBuild (WorkersHostTests).
/// </summary>
public static class WorkersComposition
{
    public static WebApplicationBuilder AddWorkersHost(this WebApplicationBuilder builder)
    {
        builder.AddPishkhanWebHost("pishkhan-workers")
            .AddPostgresCheck().AddRedisChecks().AddKafkaCheck();

        var services = builder.Services;
        var configuration = builder.Configuration;
        services.AddAuthentication();
        services.AddAuthorization();

        // No signed-in caller here: Identity, Tenancy and Content register only their core services, not the panel handlers
        // (those need ICurrentUser and TokenFactory, which only the Api and AdminApi hosts have).
        services
            .AddFilesModule(configuration)
            .AddIdentityCore(configuration)
            .AddTenancyCore(configuration)
            .AddChannelsModule(configuration)
            .AddConversationsModule(configuration)
            .AddAutomationModule(configuration)
            .AddCatalogModule(configuration)
            .AddOrdersModule(configuration)
            .AddPaymentsModule(configuration)
            .AddAgentModule(configuration).AddAgentReplies()
            .AddAuditModule(configuration)
            .AddNotificationsModule(configuration)
            .AddAnalyticsModule(configuration)
            .AddBillingModule(configuration)
            .AddKnowledgeModule(configuration)
            .AddContentCore(configuration);

        var groups = configuration.GetSection(WorkerGroupOptions.Section).Get<WorkerGroupOptions>() ?? new WorkerGroupOptions();
        services.AddPishkhanMessaging(configuration, "workers", m => m
            .AddCacheInvalidation()
            .AddConversationsIngestConsumers()
            .AddConversationsEngineConsumers()
            .AddConversationsOutboundConsumers()
            .AddConversationsPlatformConsumers()
            .AddChannelsPlatformConsumers()
            .AddAutomationEngineConsumers()
            .AddAutomationOutboundConsumers()
            .AddAutomationPlatformConsumers()
            .AddCatalogConsumers()
            .AddOrdersConsumers()
            .AddPaymentsConsumers()
            .AddFilesConsumers()
            .AddTenancyPlatformConsumers()
            .AddAuditConsumers()
            .AddNotificationsConsumers()
            .AddAnalyticsConsumers()
            .AddBillingConsumers()
            .AddKnowledgeConsumers()
            .AddAgentConsumers()
            .AddContentConsumers());

        if (groups.IsEnabled("platform"))
        {
            services.AddOutboxRelay(configuration);
            services.AddHostedService<TimerDispatcher>();
            services.AddPishkhanJobs(configuration, [.. QuartzExtensions.PlatformJobs, .. ChannelsModule.Jobs, .. TenancyModule.Jobs, .. ContentModule.Jobs, .. BillingModule.Jobs]);
        }

        return builder;
    }
}
