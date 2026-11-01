using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Knowledge.Domain;

namespace Pishkhan.Modules.Knowledge.Persistence;

public sealed class KnowledgeDbContext(DbContextOptions<KnowledgeDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "knowledge";

    public override string Schema => SchemaName;

    public DbSet<KnowledgeSource> Sources => Set<KnowledgeSource>();

    public DbSet<KnowledgeChunk> Chunks => Set<KnowledgeChunk>();

    public DbSet<UnansweredQuestion> Unanswered => Set<UnansweredQuestion>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<KnowledgeSource>(b =>
        {
            b.ToTable("sources");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Title).HasMaxLength(KnowledgeSource.MaxTitle);
            b.Property(x => x.Question).HasMaxLength(KnowledgeSource.MaxQuestion);
            b.Property(x => x.Body).HasMaxLength(KnowledgeSource.MaxBody);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Error).HasMaxLength(200);
            b.HasIndex(x => new { x.WorkspaceId, x.Kind, x.CreatedAt });
            b.HasIndex(x => new { x.WorkspaceId, x.ProductId }).IsUnique().HasFilter("product_id IS NOT NULL");
        });

        modelBuilder.Entity<KnowledgeChunk>(b =>
        {
            b.ToTable("chunks");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.EmbeddingModel).HasMaxLength(100);
            b.HasIndex(x => new { x.SourceId, x.Ordinal });
            b.HasIndex(x => x.WorkspaceId);
        });

        modelBuilder.Entity<UnansweredQuestion>(b =>
        {
            b.ToTable("unanswered_questions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Question).HasMaxLength(UnansweredQuestion.MaxQuestion);
            b.Property(x => x.Key).HasMaxLength(UnansweredQuestion.MaxQuestion);
            b.Property(x => x.Status).HasMaxLength(20);
            b.HasIndex(x => new { x.WorkspaceId, x.Key }).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.LastAskedAt });
        });
    }
}

internal sealed class KnowledgeDbContextFactory : IDesignTimeDbContextFactory<KnowledgeDbContext>
{
    public KnowledgeDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<KnowledgeDbContext>(KnowledgeDbContext.SchemaName), new TenantContext());
}
