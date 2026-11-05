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
using Pishkhan.Modules.Tenancy.Contracts;

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

    /// <summary>AdminApi: the quarantine pages. Needs <see cref="AddFilesModule"/> and the Tenancy and Backoffice modules.</summary>
    public static IServiceCollection AddFilesBackoffice(this IServiceCollection services)
    {
        services.AddScoped<QuarantineActions>();
        return services;
    }

    /// <summary>AdminApi: <c>/admin/v1/files</c>.</summary>
    public static IEndpointRouteBuilder MapFilesAdminEndpoints(this IEndpointRouteBuilder app)
    {
        QuarantineAdminEndpoints.Map(app);
        return app;
    }

    /// <summary>Consumers of the <c>media</c> worker group.</summary>
    public static MessagingBuilder AddFilesConsumers(this MessagingBuilder messaging) => messaging
        .AddConsumer<FileScanRequested, FileScanHandler>("media", new RetryPolicy(FileScanHandler.MaxAttempts, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30)))
        .AddConsumer<TimerFired, FileRescanTimerHandler>("media")
        .AddConsumer<WorkspaceDeleted, WorkspaceFilesPurgeHandler>("media");

    public static IEndpointRouteBuilder MapFilesEndpoints(this IEndpointRouteBuilder app)
    {
        FileEndpoints.Map(app);
        FileEndpoints.MapUserContent(app);
        return app;
    }
}
