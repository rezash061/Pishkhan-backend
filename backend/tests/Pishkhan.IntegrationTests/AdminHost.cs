using System.Net.Http.Headers;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Health;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Backoffice;
using Pishkhan.Modules.Content;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Hosting;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.IntegrationTests;

/// <summary>The AdminApi composition in a test server (a second WebApplicationFactory would clash with the Api host's Program).</summary>
public sealed class AdminHost : IAsyncDisposable
{
    private AdminHost(WebApplication app) => App = app;

    public WebApplication App { get; }

    public static async Task<AdminHost> StartAsync(InfraFixture infra, CancellationToken ct)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Configuration.AddInMemoryCollection(infra.Settings);
        builder.WebHost.UseTestServer();
        builder.AddPishkhanWebHost("it-admin-api").AddPostgresCheck();
        builder.Services.AddPishkhanAuthentication(builder.Configuration, builder.Environment, AuthSchemes.Staff);
        builder.Services.AddPishkhanOpenApi(ApiDocuments.Admin);
        builder.Services.AddBackofficeModule(builder.Configuration).AddContentModule(builder.Configuration).DecorateHandlers();
        builder.Services.AddPishkhanMessaging(builder.Configuration, "it-admin-api", m => m.AddCacheInvalidation());
        var app = builder.Build();
        app.UsePishkhanWebHost();
        app.MapBackofficeEndpoints();
        app.MapContentAdminEndpoints();
        await app.StartAsync(ct);
        return new AdminHost(app);
    }

    /// <summary>A client with a staff access token for <paramref name="role"/>, minted directly (sign-in itself is covered elsewhere).</summary>
    public HttpClient StaffClient(string role)
    {
        var token = App.Services.GetRequiredService<TokenFactory>()
            .CreateAccessToken(AuthSchemes.Staff, Uuid.NewV7(), Uuid.NewV7(), [new Claim(PishkhanClaims.Roles, role), new Claim(PishkhanClaims.AuthMethods, "pwd+otp")]);
        var client = App.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return client;
    }

    public ValueTask DisposeAsync() => App.DisposeAsync();
}
