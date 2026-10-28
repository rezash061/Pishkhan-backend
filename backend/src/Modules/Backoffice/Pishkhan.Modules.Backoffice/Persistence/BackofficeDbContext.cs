using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Backoffice.Domain;

namespace Pishkhan.Modules.Backoffice.Persistence;

public sealed class BackofficeDbContext(DbContextOptions<BackofficeDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "backoffice";

    public override string Schema => SchemaName;

    public DbSet<StaffUser> StaffUsers => Set<StaffUser>();

    public DbSet<StaffSession> StaffSessions => Set<StaffSession>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StaffUser>(b =>
        {
            b.ToTable("staff_users");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Username).HasMaxLength(64);
            b.HasIndex(x => x.Username).IsUnique();
            b.Property(x => x.PasswordHash).HasMaxLength(200);
            b.Property(x => x.TotpSecretEnc).HasMaxLength(512);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<StaffSession>(b =>
        {
            b.ToTable("staff_sessions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.RefreshHash).HasMaxLength(64);
            b.HasIndex(x => x.RefreshHash).IsUnique();
            b.HasIndex(x => x.FamilyId);
            b.Property(x => x.RevokedReason).HasMaxLength(40);
            b.Property(x => x.Ip).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.HasOne<StaffUser>().WithMany().HasForeignKey(x => x.StaffId).OnDelete(DeleteBehavior.Cascade);
            b.Ignore(x => x.DomainEvents);
        });
    }
}

internal sealed class BackofficeDbContextFactory : IDesignTimeDbContextFactory<BackofficeDbContext>
{
    public BackofficeDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<BackofficeDbContext>(BackofficeDbContext.SchemaName), new TenantContext());
}
