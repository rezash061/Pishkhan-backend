using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Sms;
using Pishkhan.Modules.Audit;
using Pishkhan.Modules.Automation;
using Pishkhan.Modules.Catalog;
using Pishkhan.Modules.Channels;
using Pishkhan.Modules.Conversations;
using Pishkhan.Modules.Files;
using Pishkhan.Modules.Identity;
using Pishkhan.Modules.Tenancy;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Hosting;
using Pishkhan.Web.OpenApi;

// Panel and public API (backend plan 2): composition only.
var builder = WebApplication.CreateBuilder(args);
builder.AddPishkhanWebHost("pishkhan-api")
    .AddPostgresCheck().AddRedisChecks().AddKafkaCheck().AddStorageCheck().AddClamAvCheck();

var services = builder.Services;
var configuration = builder.Configuration;
services.AddPishkhanAuthentication(configuration, builder.Environment, AuthSchemes.User, AuthSchemes.Realtime);
services.AddPishkhanOpenApi(ApiDocuments.Api, ApiDocuments.Public);
services
    .AddIdentityModule(configuration)
    .AddTenancyModule(configuration)
    .AddFilesModule(configuration)
    .AddChannelsModule(configuration)
    .AddConversationsModule(configuration)
    .AddConversationsRealtime()
    .AddAutomationModule(configuration)
    .AddCatalogModule(configuration)
    .DecorateHandlers();
services.AddPishkhanRealtime();
services.AddPishkhanMessaging(configuration, "api", m => m
    .AddCacheInvalidation()
    .AddConversationsRealtimeConsumers());

var app = builder.Build();
app.UsePishkhanWebHost();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapJwks(AuthSchemes.User);
app.MapIdentityEndpoints();
app.MapTenancyEndpoints();
app.MapFilesEndpoints();
app.MapChannelsEndpoints();
app.MapConversationsEndpoints();
app.MapAutomationEndpoints();
app.MapCatalogEndpoints();

if (app.Environment.IsDevelopment())
{
    // Development only: OTP codes and other SMS land in Redis instead of Kavenegar.
    app.MapGet("/dev/sms", async (IServiceProvider sp) =>
        sp.GetService<DevSmsSender>() is { } dev ? Results.Ok(await dev.RecentAsync()) : Results.NotFound())
        .AllowAnonymous()
        .ExcludeFromDescription();
}

await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
