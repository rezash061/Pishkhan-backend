using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Templates.Domain;

namespace Pishkhan.Modules.Templates.Persistence;

public sealed class TemplatesDbContext(DbContextOptions<TemplatesDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "templates";

    public override string Schema => SchemaName;

    /// <summary>Platform-wide: not owned by a workspace.</summary>
    public DbSet<IndustryTemplate> Templates => Set<IndustryTemplate>();

    public DbSet<TemplateApplication> Applications => Set<TemplateApplication>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IndustryTemplate>(b =>
        {
            b.ToTable("industry_templates");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Industry).HasMaxLength(30);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Name).HasMaxLength(IndustryTemplate.MaxName);
            b.Property(x => x.Description).HasMaxLength(IndustryTemplate.MaxDescription);
            b.Property(x => x.Content).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => JsonSerializer.Deserialize<TemplateContent>(v, EnvelopeJson.Options)!,
                new ValueComparer<TemplateContent>(
                    (a, c) => JsonSerializer.Serialize(a, EnvelopeJson.Options) == JsonSerializer.Serialize(c, EnvelopeJson.Options),
                    v => JsonSerializer.Serialize(v, EnvelopeJson.Options).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<TemplateContent>(JsonSerializer.Serialize(v, EnvelopeJson.Options), EnvelopeJson.Options)!));
            b.HasIndex(x => new { x.Industry, x.VersionNumber }).IsUnique();

            // One published and one draft version per industry at a time.
            b.HasIndex(x => x.Industry, "ix_industry_templates_industry_published").IsUnique().HasFilter("status = 'published'");
            b.HasIndex(x => x.Industry, "ix_industry_templates_industry_draft").IsUnique().HasFilter("status = 'draft'");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<TemplateApplication>(b =>
        {
            b.ToTable("applications");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Industry).HasMaxLength(30);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
        });
    }
}

internal sealed class TemplatesDbContextFactory : IDesignTimeDbContextFactory<TemplatesDbContext>
{
    public TemplatesDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<TemplatesDbContext>(TemplatesDbContext.SchemaName), new TenantContext());
}
