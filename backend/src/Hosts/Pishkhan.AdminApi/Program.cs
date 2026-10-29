using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Audit;
using Pishkhan.Modules.Backoffice;
using Pishkhan.Modules.Content;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Hosting;
using Pishkhan.Web.OpenApi;

// Back-office API for the admin panel. Separate host, own token audience: user tokens are rejected here and vice versa.
var builder = WebApplication.CreateBuilder(args);
builder.AddPishkhanWebHost("pishkhan-admin-api")
    .AddPostgresCheck().AddRedisChecks();

var services = builder.Services;
var configuration = builder.Configuration;
services.AddPishkhanAuthentication(configuration, builder.Environment, AuthSchemes.Staff);
services.AddPishkhanOpenApi(ApiDocuments.Admin);
services.AddBackofficeModule(configuration).AddAuditModule(configuration).AddContentModule(configuration).DecorateHandlers();
services.AddPishkhanMessaging(configuration, "admin-api", m => m.AddCacheInvalidation());

var app = builder.Build();
app.UsePishkhanWebHost();
app.MapOpenApi("/openapi/{documentName}.json").AllowAnonymous();
app.MapJwks(AuthSchemes.Staff);
app.MapBackofficeEndpoints();
app.MapContentAdminEndpoints();
app.MapAuditAdminEndpoints();
await app.RunAsync();

/// <summary>Entry point marker for WebApplicationFactory in integration tests.</summary>
public partial class Program;
