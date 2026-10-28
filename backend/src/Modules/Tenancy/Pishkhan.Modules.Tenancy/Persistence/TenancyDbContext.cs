using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Tenancy.Domain;

namespace Pishkhan.Modules.Tenancy.Persistence;

public sealed class TenancyDbContext(DbContextOptions<TenancyDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "tenancy";

    public override string Schema => SchemaName;

    public DbSet<Workspace> Workspaces => Set<Workspace>();

    public DbSet<Membership> Memberships => Set<Membership>();

    public DbSet<Invitation> Invitations => Set<Invitation>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Workspace>(b =>
        {
            b.ToTable("workspaces");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Slug).HasMaxLength(40);
            b.HasIndex(x => x.Slug).IsUnique();
            b.Property(x => x.Industry).HasMaxLength(30);
            b.Property(x => x.TimeZone).HasMaxLength(50);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.BusinessHours)
                .HasColumnType("jsonb")
                .HasConversion(
                    v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                    v => JsonSerializer.Deserialize<BusinessHours>(v, EnvelopeJson.Options) ?? BusinessHours.Default,
                    new ValueComparer<BusinessHours>((a, b) => JsonSerializer.Serialize(a, EnvelopeJson.Options) == JsonSerializer.Serialize(b, EnvelopeJson.Options), v => v.GetHashCode(), v => v));
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Membership>(b =>
        {
            b.ToTable("memberships");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.WorkspaceId, x.UserId }).IsUnique();
            b.HasIndex(x => x.UserId);
            b.Property(x => x.Role).HasMaxLength(20);
            b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Invitation>(b =>
        {
            b.ToTable("invitations");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.WorkspaceId, x.Status });
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.Property(x => x.PhoneEnc).HasMaxLength(512);
            b.Property(x => x.PhoneBidx).HasMaxLength(64);
            b.Property(x => x.TokenHash).HasMaxLength(64);
            b.Property(x => x.Role).HasMaxLength(20);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

internal sealed class TenancyDbContextFactory : IDesignTimeDbContextFactory<TenancyDbContext>
{
    public TenancyDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<TenancyDbContext>(TenancyDbContext.SchemaName), new TenantContext());
}
