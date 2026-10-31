using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Analytics.Domain;

namespace Pishkhan.Modules.Analytics.Persistence;

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "analytics";

    public override string Schema => SchemaName;

    public DbSet<HourlyMetrics> Hourly => Set<HourlyMetrics>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HourlyMetrics>(b =>
        {
            b.ToTable("hourly_metrics");
            b.HasKey(x => new { x.WorkspaceId, x.Hour });
        });
    }

    /// <summary>Adds <paramref name="delta"/> to the workspace's row for the hour of <paramref name="at"/>, creating it if needed.</summary>
    public Task AddAsync(Guid workspaceId, DateTimeOffset at, MetricsDelta delta, CancellationToken ct) =>
        Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO analytics.hourly_metrics (workspace_id, hour, messages_received, new_conversations, replies_by_rule, replies_by_ai, replies_by_human, handoffs)
            VALUES ({workspaceId}, {HourlyMetrics.HourOf(at)}, {delta.MessagesReceived}, {delta.NewConversations}, {delta.RepliesByRule}, {delta.RepliesByAi}, {delta.RepliesByHuman}, {delta.Handoffs})
            ON CONFLICT (workspace_id, hour) DO UPDATE SET
                messages_received = hourly_metrics.messages_received + EXCLUDED.messages_received,
                new_conversations = hourly_metrics.new_conversations + EXCLUDED.new_conversations,
                replies_by_rule = hourly_metrics.replies_by_rule + EXCLUDED.replies_by_rule,
                replies_by_ai = hourly_metrics.replies_by_ai + EXCLUDED.replies_by_ai,
                replies_by_human = hourly_metrics.replies_by_human + EXCLUDED.replies_by_human,
                handoffs = hourly_metrics.handoffs + EXCLUDED.handoffs
            """,
            ct);
}

internal sealed class AnalyticsDbContextFactory : IDesignTimeDbContextFactory<AnalyticsDbContext>
{
    public AnalyticsDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<AnalyticsDbContext>(AnalyticsDbContext.SchemaName), new TenantContext());
}
