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

    public DbSet<ContentEntry> Entries => Set<ContentEntry>();

    public DbSet<ContentCategory> Categories => Set<ContentCategory>();

    public DbSet<ContentRedirect> Redirects => Set<ContentRedirect>();

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

        modelBuilder.Entity<ContentEntry>(b =>
        {
            b.ToTable("entries");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Slug).HasMaxLength(ContentEntry.MaxSlug);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Title).HasMaxLength(ContentEntry.MaxTitle);
            b.Property(x => x.Description).HasMaxLength(ContentEntry.MaxDescription);
            b.Property(x => x.Category).HasMaxLength(ContentEntry.MaxSlug);
            b.Property(x => x.Author).HasMaxLength(ContentEntry.MaxAuthor);
            b.Property(x => x.LiveCategory).HasMaxLength(ContentEntry.MaxSlug);
            b.Property(x => x.Body).HasColumnType("jsonb");
            b.Property(x => x.Data).HasColumnType("jsonb");
            b.Property(x => x.Published).HasColumnType("jsonb").HasConversion(
                v => v == null ? null : JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => v == null ? null : JsonSerializer.Deserialize<EntryContent>(v, EnvelopeJson.Options));
            b.Ignore(x => x.Draft);
            b.Ignore(x => x.HasUnpublishedChanges);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.Kind, x.Slug }).IsUnique();
            b.HasIndex(x => new { x.Kind, x.Status, x.LiveCategory, x.FirstPublishedAt });
            b.HasIndex(x => x.ScheduledAt).HasFilter("scheduled_at IS NOT NULL");
        });

        modelBuilder.Entity<ContentCategory>(b =>
        {
            b.ToTable("categories");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Slug).HasMaxLength(ContentEntry.MaxSlug);
            b.Property(x => x.Title).HasMaxLength(ContentCategory.MaxTitle);
            b.Property(x => x.Description).HasMaxLength(ContentCategory.MaxDescription);
            b.HasIndex(x => new { x.Kind, x.Slug }).IsUnique();
        });

        modelBuilder.Entity<ContentRedirect>(b =>
        {
            b.ToTable("redirects");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.FromPath).HasMaxLength(ContentRedirect.MaxPath);
            b.Property(x => x.ToPath).HasMaxLength(ContentRedirect.MaxPath);
            b.HasIndex(x => x.FromPath).IsUnique();
        });
    }
}

internal sealed class ContentDbContextFactory : IDesignTimeDbContextFactory<ContentDbContext>
{
    public ContentDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<ContentDbContext>(ContentDbContext.SchemaName), new TenantContext());
}
