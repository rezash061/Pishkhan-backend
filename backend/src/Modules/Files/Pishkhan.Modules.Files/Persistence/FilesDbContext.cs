using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Files.Domain;

namespace Pishkhan.Modules.Files.Persistence;

public sealed class FilesDbContext(DbContextOptions<FilesDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "files";

    public override string Schema => SchemaName;

    public DbSet<StoredFile> Files => Set<StoredFile>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<StoredFile>(b =>
        {
            b.ToTable("files");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Purpose).HasMaxLength(40);
            b.Property(x => x.OriginalName).HasMaxLength(200);
            b.Property(x => x.ContentTypeDeclared).HasMaxLength(150);
            b.Property(x => x.ContentTypeDetected).HasMaxLength(150);
            b.Property(x => x.Sha256).HasMaxLength(64);
            b.Property(x => x.Bucket).HasMaxLength(40);
            b.Property(x => x.StorageKey).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.ScanEngineVersion).HasMaxLength(200);
            b.Property(x => x.Signature).HasMaxLength(200);
            b.Property(x => x.RejectReason).HasMaxLength(300);
            b.Property(x => x.Derived).HasColumnType("jsonb").HasConversion(
                v => JsonSerializer.Serialize(v, EnvelopeJson.Options),
                v => JsonSerializer.Deserialize<FileDerivatives>(v, EnvelopeJson.Options) ?? new FileDerivatives(null, null, null));
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.HasIndex(x => new { x.Sha256, x.Status });
            b.HasIndex(x => x.Status).HasFilter("status IN ('pending_scan', 'scan_failed', 'infected')");
            b.Ignore(x => x.DomainEvents);
        });
    }
}

internal sealed class FilesDbContextFactory : IDesignTimeDbContextFactory<FilesDbContext>
{
    public FilesDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<FilesDbContext>(FilesDbContext.SchemaName), new TenantContext());
}
