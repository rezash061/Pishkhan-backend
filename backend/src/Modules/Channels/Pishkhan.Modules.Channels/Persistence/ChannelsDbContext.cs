using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Channels.Domain;

namespace Pishkhan.Modules.Channels.Persistence;

public sealed class ChannelsDbContext(DbContextOptions<ChannelsDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "channels";

    public override string Schema => SchemaName;

    public DbSet<ChannelAccount> Accounts => Set<ChannelAccount>();

    public DbSet<DataDeletionRequest> DataDeletionRequests => Set<DataDeletionRequest>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelAccount>(b =>
        {
            b.ToTable("channel_accounts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.ExternalId).HasMaxLength(100);
            b.Property(x => x.Username).HasMaxLength(100);
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(30);
            b.Property(x => x.TokenEnc).HasMaxLength(2048);
            b.Property(x => x.RateProfile).HasMaxLength(40);
            b.HasIndex(x => new { x.Kind, x.ExternalId }).IsUnique().HasFilter("status <> 'disconnected'");
            b.HasIndex(x => new { x.WorkspaceId, x.Kind });
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<DataDeletionRequest>(b =>
        {
            b.ToTable("data_deletion_requests");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ConfirmationCode).HasMaxLength(40);
            b.HasIndex(x => x.ConfirmationCode).IsUnique();
            b.Property(x => x.ExternalUserId).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(20);
        });
    }
}

internal sealed class ChannelsDbContextFactory : IDesignTimeDbContextFactory<ChannelsDbContext>
{
    public ChannelsDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<ChannelsDbContext>(ChannelsDbContext.SchemaName), new TenantContext());
}
