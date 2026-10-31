using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Billing.Domain;

namespace Pishkhan.Modules.Billing.Persistence;

public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "billing";

    public override string Schema => SchemaName;

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<UsageCounter> UsageCounters => Set<UsageCounter>();

    public DbSet<UsageSubject> UsageSubjects => Set<UsageSubject>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Plan>(b =>
        {
            b.ToTable("plans");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.Name).HasMaxLength(50);
            b.Property(x => x.Tagline).HasMaxLength(120);
            b.Property(x => x.Description).HasMaxLength(1000);
            Json(b.Property(x => x.Limits));
            Json(b.Property(x => x.Features));
            b.HasIndex(x => new { x.Code, x.VersionNumber }).IsUnique();

            // At most one published and one draft version per plan.
            b.HasIndex(x => x.Code, "ix_plans_code_published").IsUnique().HasFilter("status = 'Published'");
            b.HasIndex(x => x.Code, "ix_plans_code_draft").IsUnique().HasFilter("status = 'Draft'");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Subscription>(b =>
        {
            b.ToTable("subscriptions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.PlanCode).HasMaxLength(30);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(x => x.EndReason).HasMaxLength(20);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });

            // One current subscription per workspace.
            b.HasIndex(x => x.WorkspaceId, "ix_subscriptions_workspace_active").IsUnique().HasFilter("status = 'Active'");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<UsageCounter>(b =>
        {
            b.ToTable("usage_counters");
            b.HasKey(x => new { x.WorkspaceId, x.Meter, x.PeriodStart });
            b.Property(x => x.Meter).HasMaxLength(40);
        });

        modelBuilder.Entity<UsageSubject>(b =>
        {
            b.ToTable("usage_subjects");
            b.HasKey(x => new { x.WorkspaceId, x.Meter, x.PeriodStart, x.SubjectId });
            b.Property(x => x.Meter).HasMaxLength(40);
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

internal sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<BillingDbContext>(BillingDbContext.SchemaName), new TenantContext());
}
