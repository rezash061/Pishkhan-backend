using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Conversations.Domain;

namespace Pishkhan.Modules.Conversations.Persistence;

public sealed class ConversationsDbContext(DbContextOptions<ConversationsDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "conversations";

    public override string Schema => SchemaName;

    public DbSet<Contact> Contacts => Set<Contact>();

    public DbSet<ChannelIdentity> ChannelIdentities => Set<ChannelIdentity>();

    public DbSet<Conversation> Conversations => Set<Conversation>();

    public DbSet<Message> Messages => Set<Message>();

    public DbSet<ExternalMessageKey> ExternalMessageKeys => Set<ExternalMessageKey>();

    public DbSet<ConversationNote> Notes => Set<ConversationNote>();

    public DbSet<QuickReplyTemplate> QuickReplies => Set<QuickReplyTemplate>();

    public DbSet<PrivateReplyKey> PrivateReplyKeys => Set<PrivateReplyKey>();

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Contact>(b =>
        {
            b.ToTable("contacts");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.DisplayName).HasMaxLength(200);
            b.Property(x => x.Username).HasMaxLength(100);
            b.HasIndex(x => x.WorkspaceId);
            b.Ignore(x => x.DomainEvents);
        });

        modelBuilder.Entity<ChannelIdentity>(b =>
        {
            b.ToTable("contacts_channel_identities");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ExternalUserId).HasMaxLength(100);
            b.HasIndex(x => new { x.ChannelAccountId, x.ExternalUserId }).IsUnique();
            b.HasIndex(x => x.ContactId);
        });

        modelBuilder.Entity<Conversation>(b =>
        {
            b.ToTable("conversations");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.ChannelKind).HasMaxLength(20);
            b.Property(x => x.ExternalUserId).HasMaxLength(100);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.LastMessagePreview).HasMaxLength(120);

            // One conversation per customer per channel account; it reopens instead of forking.
            b.HasIndex(x => new { x.ChannelAccountId, x.ContactId }).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.LastInboundAt });

            // Inbox list: newest first, keyset on (last_inbound_at, id); filtered by assignee or channel account.
            b.HasIndex(x => new { x.WorkspaceId, x.LastInboundAt, x.Id }).IsDescending(false, true, true);
            b.HasIndex(x => new { x.WorkspaceId, x.AssigneeId, x.LastInboundAt });
            b.HasIndex(x => new { x.WorkspaceId, x.LastInboundAt }).HasFilter("unread_count > 0").HasDatabaseName("ix_conversations_unread");
            b.Ignore(x => x.DomainEvents);
        });

        // Partitioned by month on created_at. The migration creates the parent table by hand (PARTITION BY RANGE).
        modelBuilder.Entity<Message>(b =>
        {
            b.ToTable("messages");
            b.HasKey(x => new { x.Id, x.CreatedAt });
            b.Property(x => x.Direction).HasMaxLength(10);
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.Text).HasMaxLength(4000);
            b.Property(x => x.NormalizedText).HasMaxLength(4000);
            b.Property(x => x.AttachmentsJson).HasColumnType("jsonb");
            b.Property(x => x.ExternalId).HasMaxLength(200);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.ErrorCode).HasMaxLength(80);
            b.Property(x => x.HandledBy).HasMaxLength(10);
            b.Property(x => x.ContentJson).HasColumnType("jsonb");
            b.Property(x => x.Tag).HasMaxLength(40);
            b.Property(x => x.ReplyToCommentId).HasMaxLength(100);
            b.HasIndex(x => new { x.ConversationId, x.CreatedAt, x.Id });
        });

        modelBuilder.Entity<ConversationNote>(b =>
        {
            b.ToTable("conversation_notes");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Text).HasMaxLength(2000);
            b.HasIndex(x => new { x.ConversationId, x.CreatedAt });
        });

        modelBuilder.Entity<QuickReplyTemplate>(b =>
        {
            b.ToTable("quick_replies");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Shortcut).HasMaxLength(40);
            b.Property(x => x.Text).HasMaxLength(1000);
            b.HasIndex(x => new { x.WorkspaceId, x.Shortcut }).IsUnique();
        });

        modelBuilder.Entity<PrivateReplyKey>(b =>
        {
            b.ToTable("private_reply_keys");
            b.HasKey(x => new { x.ChannelAccountId, x.CommentId });
            b.Property(x => x.CommentId).HasMaxLength(100);
        });

        modelBuilder.Entity<ExternalMessageKey>(b =>
        {
            b.ToTable("external_message_keys");
            b.HasKey(x => new { x.ChannelAccountId, x.ExternalId });
            b.Property(x => x.ExternalId).HasMaxLength(200);
        });
    }
}

internal sealed class ConversationsDbContextFactory : IDesignTimeDbContextFactory<ConversationsDbContext>
{
    public ConversationsDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<ConversationsDbContext>(ConversationsDbContext.SchemaName), new TenantContext());
}
