using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Audit.Domain;

namespace Pishkhan.Modules.Audit.Persistence;

public sealed class AuditDbContext(DbContextOptions<AuditDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "audit";

    public override string Schema => SchemaName;

    public DbSet<AuditEntry> Entries => Set<AuditEntry>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        // Partitioned by month on occurred_at. The migration creates the parent table by hand (PARTITION BY RANGE).
        modelBuilder.Entity<AuditEntry>(b =>
        {
            b.ToTable("audit_entries");
            b.HasKey(x => new { x.Id, x.OccurredAt });
            b.Property(x => x.ActorType).HasMaxLength(10);
            b.Property(x => x.ActorName).HasMaxLength(100);
            b.Property(x => x.Action).HasMaxLength(80);
            b.Property(x => x.EntityType).HasMaxLength(60);
            b.Property(x => x.EntityId).HasMaxLength(100);
            b.Property(x => x.Reason).HasMaxLength(500);
            b.Property(x => x.Ip).HasMaxLength(45);
            b.Property(x => x.BeforeJson).HasColumnName("before").HasColumnType("jsonb");
            b.Property(x => x.AfterJson).HasColumnName("after").HasColumnType("jsonb");
            b.HasIndex(x => new { x.OccurredAt, x.Id }).IsDescending(true, true);
            b.HasIndex(x => new { x.WorkspaceId, x.OccurredAt }).IsDescending(false, true);
            b.HasIndex(x => new { x.ActorId, x.OccurredAt }).IsDescending(false, true);
            b.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}

internal sealed class AuditDbContextFactory : IDesignTimeDbContextFactory<AuditDbContext>
{
    public AuditDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<AuditDbContext>(AuditDbContext.SchemaName), new TenantContext());
}
