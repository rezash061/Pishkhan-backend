using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Files.Contracts;
using Pishkhan.Modules.Files.Features;
using Pishkhan.Modules.Files.Persistence;
using Pishkhan.Modules.Files.Services;

namespace Pishkhan.Modules.Files;

public static class FilesModule
{
    public static IServiceCollection AddFilesModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<FilesDbContext>(FilesDbContext.SchemaName);
        services.AddScoped<FileIntake>();
        services.AddScoped<IFileService>(sp => sp.GetRequiredService<FileIntake>());
        return services;
    }

    /// <summary>Consumers of the <c>media</c> worker group.</summary>
    public static MessagingBuilder AddFilesConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<FileScanRequested, FileScanHandler>("media", new RetryPolicy(FileScanHandler.MaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)))
        .AddConsumer<TimerFired, FileRescanTimerHandler>("media");

    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        FileEndpoints.Map(app);
        FileEndpoints.MapUserContent(app);
        return app;
    }
}
