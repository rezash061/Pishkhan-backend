using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Catalog.Domain;

namespace Pishkhan.Modules.Catalog.Persistence;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "catalog";

    public override string Schema => SchemaName;

    public DbSet<Category> Categories => Set<Category>();

    public DbSet<Product> Products => Set<Product>();

    public DbSet<ProductVariant> Variants => Set<ProductVariant>();

    public DbSet<StorefrontCardSet> CardSets => Set<StorefrontCardSet>();

    public DbSet<CatalogImport> Imports => Set<CatalogImport>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Category>(b =>
        {
            b.ToTable("categories");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => new { x.WorkspaceId, x.Name }).IsUnique().HasFilter("deleted_at IS NULL");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Product>(b =>
        {
            b.ToTable("products");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Title).HasMaxLength(CatalogValidator.MaxTitle);
            b.Property(x => x.Description).HasMaxLength(CatalogValidator.MaxDescription);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.PricingMode).HasMaxLength(20);
            b.Property(x => x.LinkUrl).HasMaxLength(500);
            b.HasMany(x => x.Variants).WithOne().HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(x => x.Variants).AutoInclude();
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.UpdatedAt }).HasFilter("deleted_at IS NULL");
            b.HasIndex(x => x.Tags).HasMethod("gin");
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.InStock);
        });

        modelBuilder.Entity<ProductVariant>(b =>
        {
            b.ToTable("variants");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Sku).HasMaxLength(CatalogValidator.MaxSku);
            b.Property(x => x.StockPolicy).HasMaxLength(20);
            Json(b.Property(x => x.Attributes));
            b.HasIndex(x => new { x.WorkspaceId, x.Sku }).IsUnique().HasFilter("sku IS NOT NULL AND deleted_at IS NULL");
            b.Ignore(x => x.InStock);
            b.Ignore(x => x.Label);
        });

        modelBuilder.Entity<StorefrontCardSet>(b =>
        {
            b.ToTable("card_sets");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            Json(b.Property(x => x.Items));
            b.HasIndex(x => x.WorkspaceId).HasFilter("deleted_at IS NULL");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<CatalogImport>(b =>
        {
            b.ToTable("imports");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Error).HasMaxLength(100);
            Json(b.Property(x => x.Errors));
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.Ignore(x => x.DomainEvents);
        });
    }

    private static void Json<T>(PropertyBuilder<T> property)
        where T : class
    {
        property.HasColumnType("jsonb").HasConversion(
            v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
            v => JsonSerializer.Deserialize<T>(v, EnvelopeJson.Options)!,
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, EnvelopeJson.Options) == JsonSerializer.Serialize(b, EnvelopeJson.Options),
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options).GetHashCode(StringComparison.Ordinal),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, EnvelopeJson.Options), EnvelopeJson.Options)!));
    }
}

internal sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<CatalogDbContext>(CatalogDbContext.SchemaName), new TenantContext());
}
