using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Channels;
using Pishkhan.Web.Hosting;

// Must answer Meta within seconds even when the rest of the platform is slow: only Redis (dedupe) and Kafka are on the path.
var builder = WebApplication.CreateBuilder(args);
builder.AddPishkhanWebHost("pishkhan-ingress")
    .AddRedisChecks().AddKafkaCheck();
builder.Services.AddAuthorization();
builder.Services.AddChannelsIngress(builder.Configuration);
builder.Services.AddPishkhanMessaging(builder.Configuration, "ingress", _ => { });

var app = builder.Build();
app.UsePishkhanWebHost();
app.MapChannelsIngress();
await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
