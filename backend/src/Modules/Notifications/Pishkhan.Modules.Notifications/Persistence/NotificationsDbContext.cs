using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Notifications.Domain;

namespace Pishkhan.Modules.Notifications.Persistence;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "notifications";

    public override string Schema => SchemaName;

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> Preferences => Set<NotificationPreference>();

    public DbSet<SmsLog> SmsLog => Set<SmsLog>();

    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("notifications");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(40);
            b.Property(x => x.Title).HasMaxLength(200);
            b.Property(x => x.Body).HasMaxLength(1000);
            b.Property(x => x.Link).HasMaxLength(300);
            b.HasIndex(x => new { x.WorkspaceId, x.UserId, x.CreatedAt });
            b.HasIndex(x => new { x.WorkspaceId, x.UserId }).HasFilter("read_at IS NULL");
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("preferences");
            b.HasKey(x => new { x.WorkspaceId, x.UserId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(40);
        });

        modelBuilder.Entity<SmsLog>(b =>
        {
            b.ToTable("sms_log");
            b.HasKey(x => new { x.WorkspaceId, x.UserId, x.Kind });
            b.Property(x => x.Kind).HasMaxLength(40);
        });

        modelBuilder.Entity<PushSubscription>(b =>
        {
            b.ToTable("push_subscriptions");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Endpoint).HasMaxLength(PushSubscription.MaxEndpoint);
            b.Property(x => x.P256dh).HasMaxLength(100);
            b.Property(x => x.Auth).HasMaxLength(50);
            b.Property(x => x.UserAgent).HasMaxLength(300);
            b.HasIndex(x => x.Endpoint).IsUnique();
            b.HasIndex(x => x.UserId);
        });
    }
}

internal sealed class NotificationsDbContextFactory : IDesignTimeDbContextFactory<NotificationsDbContext>
{
    public NotificationsDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<NotificationsDbContext>(NotificationsDbContext.SchemaName), new TenantContext());
}
