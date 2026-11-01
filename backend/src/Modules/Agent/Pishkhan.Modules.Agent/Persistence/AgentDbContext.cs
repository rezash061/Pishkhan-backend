using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Agent.Domain;

namespace Pishkhan.Modules.Agent.Persistence;

public sealed class AgentDbContext(DbContextOptions<AgentDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "agent";

    public override string Schema => SchemaName;

    public DbSet<EvalSet> EvalSets => Set<EvalSet>();

    public DbSet<EvalCase> EvalCases => Set<EvalCase>();

    public DbSet<EvalRun> EvalRuns => Set<EvalRun>();

    public DbSet<EvalResult> EvalResults => Set<EvalResult>();

    public DbSet<AiProvider> AiProviders => Set<AiProvider>();

    public DbSet<AiRoute> AiRoutes => Set<AiRoute>();

    public DbSet<PromptVersion> PromptVersions => Set<PromptVersion>();

    public DbSet<PromptActivation> PromptActivations => Set<PromptActivation>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<EvalSet>(b =>
        {
            b.ToTable("eval_sets");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<EvalCase>(b =>
        {
            b.ToTable("eval_cases");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ExternalRef).HasMaxLength(100);
            b.Property(x => x.Vertical).HasMaxLength(40);
            b.Property(x => x.Input).HasMaxLength(4000);
            b.Property(x => x.ContextJson).HasColumnType("jsonb");
            b.Property(x => x.ExpectedIntent).HasMaxLength(40);
            b.HasIndex(x => new { x.SetId, x.ExternalRef }).IsUnique();
            b.HasOne<EvalSet>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvalRun>(b =>
        {
            b.ToTable("eval_runs");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Task).HasMaxLength(40);
            b.Property(x => x.PromptKey).HasMaxLength(100);
            b.Property(x => x.PromptVersion).HasMaxLength(40);
            b.Property(x => x.Route).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.IntentAccuracy).HasPrecision(5, 4);
            b.HasIndex(x => new { x.SetId, x.StartedAt });
            b.HasOne<EvalSet>().WithMany().HasForeignKey(x => x.SetId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvalResult>(b =>
        {
            b.ToTable("eval_results");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Output).HasMaxLength(8000);
            b.Property(x => x.PredictedIntent).HasMaxLength(40);
            b.Property(x => x.Route).HasMaxLength(200);
            b.Property(x => x.Error).HasMaxLength(1000);
            b.HasIndex(x => x.RunId);
            b.HasOne<EvalRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<EvalCase>().WithMany().HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AiProvider>(b =>
        {
            b.ToTable("ai_providers");
            b.HasKey(x => x.Name);
            b.Property(x => x.Name).HasMaxLength(40);
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.BaseUrl).HasMaxLength(500);
            b.Property(x => x.LastTestError).HasMaxLength(1000);
        });

        modelBuilder.Entity<AiRoute>(b =>
        {
            b.ToTable("ai_routes");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Task).HasMaxLength(40);
            b.Property(x => x.Provider).HasMaxLength(40);
            b.Property(x => x.Model).HasMaxLength(200);
            b.Property(x => x.InputPricePerMillion).HasPrecision(18, 2);
            b.Property(x => x.OutputPricePerMillion).HasPrecision(18, 2);
            b.HasIndex(x => new { x.Task, x.Position }).IsUnique();
        });

        modelBuilder.Entity<PromptVersion>(b =>
        {
            b.ToTable("prompt_versions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Key).HasMaxLength(100);
            b.Property(x => x.Version).HasMaxLength(40);
            b.Property(x => x.BaseVersion).HasMaxLength(40);
            b.Property(x => x.Text).HasMaxLength(20000);
            b.HasIndex(x => new { x.Key, x.Version }).IsUnique();
        });

        modelBuilder.Entity<PromptActivation>(b =>
        {
            b.ToTable("prompt_activations");
            b.HasKey(x => x.Key);
            b.Property(x => x.Key).HasMaxLength(100);
            b.Property(x => x.Version).HasMaxLength(40);
        });
    }
}

internal sealed class AgentDbContextFactory : IDesignTimeDbContextFactory<AgentDbContext>
{
    public AgentDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<AgentDbContext>(AgentDbContext.SchemaName), new TenantContext());
}
