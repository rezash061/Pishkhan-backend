using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Identity.Domain;

namespace Pishkhan.Modules.Identity.Persistence;

public sealed class IdentityDbContext(DbContextOptions<IdentityDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "identity";

    public override string Schema => SchemaName;

    public DbSet<User> Users => Set<User>();

    public DbSet<Session> Sessions => Set<Session>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(b =>
        {
            b.ToTable("users");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PhoneE164Enc).HasMaxLength(512);
            b.Property(x => x.PhoneBidx).HasMaxLength(64);
            b.HasIndex(x => x.PhoneBidx).IsUnique();
            b.Property(x => x.DisplayName).HasMaxLength(100);
            b.Property(x => x.PasswordHash).HasMaxLength(200);
            b.Property(x => x.TotpSecretEnc).HasMaxLength(512);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Session>(b =>
        {
            b.ToTable("sessions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.RefreshHash).HasMaxLength(64);
            b.HasIndex(x => x.RefreshHash).IsUnique();
            b.HasIndex(x => new { x.UserId, x.FamilyId });
            b.HasIndex(x => x.FamilyId);
            b.Property(x => x.RevokedReason).HasMaxLength(40);
            b.Property(x => x.Ip).HasMaxLength(64);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.Property(x => x.AuthMethods).HasMaxLength(40);
            b.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            b.Ignore(x => x.DomainEvents);
        });
    }
}

internal sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<IdentityDbContext>(IdentityDbContext.SchemaName), new TenantContext());
}
