using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Pishkhan.Workers;

namespace Pishkhan.UnitTests;

public class WorkersHostTests
{
    /// <summary>
    /// The Workers host builds with every worker group on and ValidateOnBuild, so a registration that only the Api host
    /// satisfies (ICurrentUser, TokenFactory) fails here instead of at startup. Nothing is started: no Kafka or database needed.
    /// </summary>
    [Fact]
    public async Task Workers_host_resolves_every_registration()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Host.UseDefaultServiceProvider(o =>
        {
            o.ValidateOnBuild = true;
            o.ValidateScopes = true;
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Workers:Groups:0"] = "*" });

        builder.AddWorkersHost();

        await using var app = builder.Build();
        app.Services.ShouldNotBeNull();
    }
}
