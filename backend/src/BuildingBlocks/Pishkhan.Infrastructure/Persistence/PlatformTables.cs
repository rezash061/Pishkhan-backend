using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>
/// Mapping of the shared <c>platform</c> tables. <see cref="PlatformDbContext"/> owns their migrations; every module context maps
/// the outbox and inbox too (excluded from its migrations) so it can write them in its own transaction.
/// </summary>
public static class PlatformTables
{
    public const string Schema = "platform";

    public static void MapOutbox(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        modelBuilder.Entity<OutboxMessage>(b =>
        {
            b.ToTable("outbox_messages", Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Topic).HasMaxLength(200);
            b.Property(x => x.Key).HasMaxLength(200);
            b.Property(x => x.Type).HasMaxLength(200);
            b.Property(x => x.Envelope).HasColumnType("jsonb");
            b.Property(x => x.Headers).HasColumnType("jsonb");
            b.HasIndex(x => new { x.Shard, x.Id }).HasFilter("published_at IS NULL").HasDatabaseName("ix_outbox_unpublished");
            b.HasIndex(x => x.PublishedAt).HasFilter("published_at IS NOT NULL").HasDatabaseName("ix_outbox_published_at");
        });
    }

    public static void MapInbox(ModelBuilder modelBuilder, bool excludeFromMigrations)
    {
        modelBuilder.Entity<ProcessedMessage>(b =>
        {
            b.ToTable("processed_messages", Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
            b.HasKey(x => new { x.Consumer, x.MessageId });
            b.Property(x => x.Consumer).HasMaxLength(200);
            b.HasIndex(x => x.ProcessedAt);
        });
    }

    public static void MapTimers(EntityTypeBuilder<TimerEntry> b, bool excludeFromMigrations)
    {
        b.ToTable("timers", Schema, t => t.ExcludeFromMigrations(excludeFromMigrations));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.Kind).HasMaxLength(100);
        b.Property(x => x.DedupeKey).HasMaxLength(300);
        b.Property(x => x.Payload).HasColumnType("jsonb");
        b.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
        b.HasIndex(x => x.DedupeKey).IsUnique().HasFilter("dedupe_key IS NOT NULL AND status = 'Pending'");
        b.HasIndex(x => x.DueAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_timers_due");
    }
}
