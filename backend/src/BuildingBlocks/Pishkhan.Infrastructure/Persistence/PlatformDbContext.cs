using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>Owns the <c>platform</c> schema: outbox, inbox and timers. Quartz tables are created by the Migrator from SQL.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public override string Schema => PlatformTables.Schema;

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        // Outbox, inbox and timers are mapped by the base class; this context owns their migrations.
    }
}

internal sealed class PlatformDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<PlatformDbContext>(PlatformTables.Schema), new Tenancy.TenantContext());
}
