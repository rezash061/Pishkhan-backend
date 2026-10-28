using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Pishkhan.Infrastructure.Tenancy;

namespace Pishkhan.Infrastructure.Persistence;

public static class PersistenceExtensions
{
    public const string ConnectionStringName = "Postgres";

    /// <summary>Registers the shared Npgsql data source (one pool per process) and the tenant context.</summary>
    public static IServiceCollection AddPishkhanPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.TryAddScoped<ITenantContext, TenantContext>();
        services.TryAddSingleton(_ =>
        {
            var connectionString = configuration.GetConnectionString(ConnectionStringName)
                ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
            var builder = new NpgsqlDataSourceBuilder(connectionString);
            builder.EnableDynamicJson();
            return builder.Build();
        });

        services.AddModuleDbContext<PlatformDbContext>("platform");
        return services;
    }

    /// <summary>Registers a module DbContext against the shared data source with snake_case naming and a per-schema migrations table.</summary>
    public static IServiceCollection AddModuleDbContext<TContext>(this IServiceCollection services, string schema)
        where TContext : ModuleDbContext
    {
        services.AddDbContext<TContext>((sp, options) => Configure(options, sp.GetRequiredService<NpgsqlDataSource>(), schema));
        return services;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder options, NpgsqlDataSource dataSource, string schema)
    {
        options.UseNpgsql(dataSource, npgsql =>
            {
                npgsql.MigrationsHistoryTable("__ef_migrations_history", schema);
                npgsql.EnableRetryOnFailure(3);
            })
            .UseSnakeCaseNamingConvention()
            .ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        return options;
    }

    /// <summary>Options for design-time factories (dotnet ef) and tests.</summary>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema, string? connectionString = null)
        where TContext : DbContext
    {
        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseNpgsql(connectionString ?? "Host=localhost;Database=pishkhan;Username=pishkhan;Password=pishkhan", o => o.MigrationsHistoryTable("__ef_migrations_history", schema))
            .UseSnakeCaseNamingConvention();
        return builder.Options;
    }
}
