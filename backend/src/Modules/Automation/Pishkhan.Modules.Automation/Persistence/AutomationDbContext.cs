using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Automation.Domain;

namespace Pishkhan.Modules.Automation.Persistence;

/// <summary>Next free send slot per channel account and call family (comment pacing).</summary>
public sealed class PacingSlot
{
    public Guid ChannelAccountId { get; init; }

    public required string Family { get; init; }

    public DateTimeOffset NextSlotAt { get; init; }
}

public sealed class AutomationDbContext(DbContextOptions<AutomationDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "automation";

    public override string Schema => SchemaName;

    public DbSet<AutomationRule> Rules => Set<AutomationRule>();

    public DbSet<Flow> Flows => Set<Flow>();

    public DbSet<FlowVersion> FlowVersions => Set<FlowVersion>();

    public DbSet<FlowRun> FlowRuns => Set<FlowRun>();

    public DbSet<AutomationSettings> Settings => Set<AutomationSettings>();

    public DbSet<CommentEvent> CommentEvents => Set<CommentEvent>();

    public DbSet<BacklogRun> BacklogRuns => Set<BacklogRun>();

    public DbSet<RuleStatsDay> RuleStats => Set<RuleStatsDay>();

    public DbSet<RuleFiring> RuleFirings => Set<RuleFiring>();

    public DbSet<PacingSlot> Pacing => Set<PacingSlot>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AutomationRule>(b =>
        {
            b.ToTable("rules");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.TriggerKind).HasMaxLength(40);
            b.Property(x => x.PostExternalId).HasMaxLength(100);
            Json(b.Property(x => x.Trigger));
            Json(b.Property(x => x.Actions));
            b.HasIndex(x => new { x.WorkspaceId, x.Enabled }).HasFilter("deleted_at IS NULL");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Flow>(b =>
        {
            b.ToTable("flows");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(20);
            Json(b.Property(x => x.Draft));
            b.HasIndex(x => x.WorkspaceId).HasFilter("deleted_at IS NULL");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<FlowVersion>(b =>
        {
            b.ToTable("flow_versions");
            b.HasKey(x => new { x.FlowId, x.Version });
            Json(b.Property(x => x.Graph));
        });

        modelBuilder.Entity<FlowRun>(b =>
        {
            b.ToTable("flow_runs");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ChannelKind).HasMaxLength(20);
            b.Property(x => x.ExternalUserId).HasMaxLength(100);
            b.Property(x => x.CurrentNode).HasMaxLength(40);
            b.Property(x => x.Status).HasMaxLength(20);
            Json(b.Property(x => x.Variables));

            // At most one active run per conversation: starting a flow cancels the previous one.
            b.HasIndex(x => x.ConversationId).IsUnique().HasFilter("status IN ('waiting_input', 'waiting_timer')").HasDatabaseName("ux_flow_runs_active");
            b.HasIndex(x => new { x.FlowId, x.Status });
            b.HasIndex(x => x.ExternalUserId);
        });

        modelBuilder.Entity<AutomationSettings>(b =>
        {
            b.ToTable("settings");
            b.HasKey(x => x.WorkspaceId);
            b.Property(x => x.FallbackText).HasMaxLength(1000);
        });

        modelBuilder.Entity<CommentEvent>(b =>
        {
            b.ToTable("comment_events");
            b.HasKey(x => new { x.ChannelAccountId, x.CommentId });
            b.Property(x => x.CommentId).HasMaxLength(100);
            b.Property(x => x.MediaId).HasMaxLength(100);
            b.Property(x => x.FromExternalId).HasMaxLength(100);
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Source).HasMaxLength(20);
            b.Property(x => x.Outcome).HasMaxLength(20);
            b.Property(x => x.PublicReplyStatus).HasMaxLength(20);
            b.Property(x => x.PublicReplyId).HasMaxLength(100);
            b.HasIndex(x => new { x.WorkspaceId, x.MediaId });
            b.HasIndex(x => x.FromExternalId);
            b.HasIndex(x => new { x.RuleId, x.ReceivedAt });
        });

        modelBuilder.Entity<BacklogRun>(b =>
        {
            b.ToTable("backlog_runs");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.MediaId).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Error).HasMaxLength(100);
            b.HasIndex(x => x.RuleId).IsUnique().HasFilter("status = 'running'").HasDatabaseName("ux_backlog_runs_running");
        });

        modelBuilder.Entity<RuleStatsDay>(b =>
        {
            b.ToTable("rule_stats");
            b.HasKey(x => new { x.RuleId, x.Day });
        });

        modelBuilder.Entity<RuleFiring>(b =>
        {
            b.ToTable("rule_firings");
            b.HasKey(x => new { x.RuleId, x.ConversationId });
        });

        modelBuilder.Entity<PacingSlot>(b =>
        {
            b.ToTable("pacing");
            b.HasKey(x => new { x.ChannelAccountId, x.Family });
            b.Property(x => x.Family).HasMaxLength(20);
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

internal sealed class AutomationDbContextFactory : IDesignTimeDbContextFactory<AutomationDbContext>
{
    public AutomationDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<AutomationDbContext>(AutomationDbContext.SchemaName), new TenantContext());
}
