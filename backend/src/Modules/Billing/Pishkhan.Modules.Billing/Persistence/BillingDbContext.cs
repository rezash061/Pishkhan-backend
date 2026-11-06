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

    /// <summary>Invoice numbers, one sequence across Pishkhan.</summary>
    public const string InvoiceSequence = "invoice_numbers";

    public override string Schema => SchemaName;

    public DbSet<Plan> Plans => Set<Plan>();

    public DbSet<Subscription> Subscriptions => Set<Subscription>();

    public DbSet<Checkout> Checkouts => Set<Checkout>();

    public DbSet<UsageCounter> UsageCounters => Set<UsageCounter>();

    public DbSet<UsageSubject> UsageSubjects => Set<UsageSubject>();

    public DbSet<BillingSettings> Settings => Set<BillingSettings>();

    public DbSet<OverageCharge> OverageCharges => Set<OverageCharge>();

    public DbSet<BillingAlert> Alerts => Set<BillingAlert>();

    public DbSet<Refund> Refunds => Set<Refund>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<WalletEntry> WalletEntries => Set<WalletEntry>();

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
            b.Property(x => x.Cycle).HasMaxLength(20);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });

            // One current subscription per workspace.
            b.HasIndex(x => x.WorkspaceId, "ix_subscriptions_workspace_active").IsUnique().HasFilter("status = 'Active'");
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.HasSequence<long>(InvoiceSequence).StartsAt(1001);

        modelBuilder.Entity<Checkout>(b =>
        {
            b.ToTable("checkouts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.PlanCode).HasMaxLength(30);
            b.Property(x => x.PlanName).HasMaxLength(100);
            b.Property(x => x.Cycle).HasMaxLength(20);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.Authority).HasMaxLength(64);
            b.Property(x => x.ReferenceId).HasMaxLength(64);
            b.Property(x => x.CardMask).HasMaxLength(32);
            b.Property(x => x.Error).HasMaxLength(40);
            b.Property(x => x.CouponCode).HasMaxLength(32);
            b.HasIndex(x => x.CouponId).HasFilter("coupon_id IS NOT NULL");
            b.HasIndex(x => x.Authority).IsUnique().HasFilter("authority IS NOT NULL");
            b.HasIndex(x => x.InvoiceNumber).IsUnique().HasFilter("invoice_number IS NOT NULL");
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.HasIndex(x => new { x.Status, x.PaidAt });
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<Refund>(b =>
        {
            b.ToTable("refunds");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Reason).HasMaxLength(300);
            b.Property(x => x.ReferenceId).HasMaxLength(64);
            b.HasIndex(x => x.CheckoutId);
            b.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<Coupon>(b =>
        {
            b.ToTable("coupons");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Code).HasMaxLength(32);
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Description).HasMaxLength(Coupon.MaxDescription);
            b.HasIndex(x => x.Code).IsUnique();
            b.HasIndex(x => x.CreatedAt);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<CouponRedemption>(b =>
        {
            b.ToTable("coupon_redemptions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.CheckoutId).IsUnique();
            b.HasIndex(x => new { x.CouponId, x.WorkspaceId });
        });

        modelBuilder.Entity<Wallet>(b =>
        {
            b.ToTable("wallets");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => x.WorkspaceId).IsUnique();
            b.ToTable(t => t.HasCheckConstraint("ck_wallets_balance", "balance >= 0"));
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<WalletEntry>(b =>
        {
            b.ToTable("wallet_entries");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Reason).HasMaxLength(WalletEntry.MaxReason);
            b.Property(x => x.ActorKind).HasMaxLength(10);
            b.HasIndex(x => new { x.WorkspaceId, x.CreatedAt });
            b.HasIndex(x => x.CheckoutId).HasFilter("checkout_id IS NOT NULL");
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

        modelBuilder.Entity<BillingSettings>(b =>
        {
            b.ToTable("billing_settings");
            b.HasKey(x => x.WorkspaceId);
        });

        modelBuilder.Entity<OverageCharge>(b =>
        {
            b.ToTable("overage_charges");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.HasIndex(x => new { x.WorkspaceId, x.PeriodStart }).IsUnique();
            b.HasIndex(x => x.CheckoutId);
            b.Property(x => x.Status).HasMaxLength(20);
        });

        modelBuilder.Entity<BillingAlert>(b =>
        {
            b.ToTable("billing_alerts");
            b.HasKey(x => new { x.WorkspaceId, x.Key });
            b.Property(x => x.Key).HasMaxLength(80);
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
