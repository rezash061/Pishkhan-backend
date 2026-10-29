using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Content.Domain;

namespace Pishkhan.Modules.Content.Persistence;

public sealed class ContentDbContext(DbContextOptions<ContentDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "content";

    public override string Schema => SchemaName;

    public DbSet<Lead> Leads => Set<Lead>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Lead>(b =>
        {
            b.ToTable("leads");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Type).HasMaxLength(20);
            b.Property(x => x.PhoneEnc).HasMaxLength(300);
            b.Property(x => x.PhoneBidx).HasMaxLength(64);
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.InstagramHandle).HasMaxLength(30);
            b.Property(x => x.Vertical).HasMaxLength(40);
            b.Property(x => x.MonthlyDms).HasMaxLength(40);
            b.Property(x => x.Message).HasMaxLength(2100);
            b.Property(x => x.Ip).HasMaxLength(45);
            b.Property(x => x.Utm).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => JsonSerializer.Deserialize<Dictionary<string, string>>(v, EnvelopeJson.Options) ?? new Dictionary<string, string>(),
                new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<Dictionary<string, string>>(
                    (a, b) => JsonSerializer.Serialize(a, EnvelopeJson.Options) == JsonSerializer.Serialize(b, EnvelopeJson.Options),
                    v => JsonSerializer.Serialize(v, EnvelopeJson.Options).GetHashCode(StringComparison.Ordinal),
                    v => new Dictionary<string, string>(v, StringComparer.Ordinal)));
            b.HasIndex(x => new { x.PhoneBidx, x.Type, x.CreatedAt });
            b.HasIndex(x => new { x.CreatedAt, x.Id }).IsDescending(true, true);
        });
    }
}

internal sealed class ContentDbContextFactory : IDesignTimeDbContextFactory<ContentDbContext>
{
    public ContentDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<ContentDbContext>(ContentDbContext.SchemaName), new TenantContext());
}
