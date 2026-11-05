using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Tenancy;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>Owns the <c>platform</c> schema: outbox, inbox and timers. Quartz tables are created by the Migrator from SQL.</summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public override string Schema => PlatformTables.Schema;

    public DbSet<DeadLetter> DeadLetters => Set<DeadLetter>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        // Outbox, inbox and timers are mapped by the base class; this context owns their migrations.
        modelBuilder.Entity<DeadLetter>(b =>
        {
            b.ToTable("dead_letters");
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Topic).HasMaxLength(200);
            b.Property(x => x.ConsumerGroup).HasMaxLength(100);
            b.Property(x => x.Consumer).HasMaxLength(300);
            b.Property(x => x.Key).HasMaxLength(200);
            b.Property(x => x.Type).HasMaxLength(200);
            b.Property(x => x.Envelope).HasColumnType("jsonb");
            b.Property(x => x.ExceptionType).HasMaxLength(300);
            b.Property(x => x.ExceptionMessage).HasMaxLength(2000);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.ResolutionReason).HasMaxLength(300);
            b.HasIndex(x => new { x.Status, x.ConsumerGroup, x.FailedAt });
            b.HasIndex(x => x.MessageId);
        });
    }
}

internal sealed class PlatformDbContextFactory : Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<PlatformDbContext>(PlatformTables.Schema), new Tenancy.TenantContext());
}
