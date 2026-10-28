using System.Diagnostics;
using System.Linq.Expressions;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.SharedKernel;

namespace Pishkhan.Infrastructure.Persistence;

/// <summary>
/// Base for every module's DbContext: one schema per module, snake_case, tenant and soft-delete query filters,
/// and the shared outbox/inbox tables for transactional messaging.
/// </summary>
public abstract class ModuleDbContext : DbContext
{
    public const string TenantFilter = "tenant";
    public const string SoftDeleteFilter = "soft_delete";
    public const string ConcurrencyTokenProperty = "Version";

    protected ModuleDbContext(DbContextOptions options, ITenantContext tenant)
        : base(options)
    {
        Tenant = tenant;
    }

    /// <summary>The PostgreSQL schema owned by this module.</summary>
    public abstract string Schema { get; }

    public ITenantContext Tenant { get; }

    /// <summary>Evaluated per query by the tenant filter. Null means "no tenant": tenant-owned queries return nothing.</summary>
    protected Guid? CurrentWorkspaceId => Tenant.WorkspaceId;

    protected bool IsSystemScope => Tenant.IsSystem;

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<ProcessedMessage> ProcessedMessages => Set<ProcessedMessage>();

    public DbSet<TimerEntry> Timers => Set<TimerEntry>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("pg_trgm");
        PlatformTables.MapOutbox(modelBuilder, excludeFromMigrations: this is not PlatformDbContext);
        PlatformTables.MapInbox(modelBuilder, excludeFromMigrations: this is not PlatformDbContext);
        PlatformTables.MapTimers(modelBuilder.Entity<TimerEntry>(), excludeFromMigrations: this is not PlatformDbContext);

        ConfigureModule(modelBuilder);
        ApplyConventions(modelBuilder);
    }

    protected abstract void ConfigureModule(ModelBuilder modelBuilder);

    private void ApplyConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(e => !e.IsOwned()).ToList())
        {
            var clr = entityType.ClrType;
            var builder = modelBuilder.Entity(clr);

            if (typeof(ITenantOwned).IsAssignableFrom(clr))
            {
                builder.HasQueryFilter(TenantFilter, BuildTenantFilter(clr));
            }

            if (typeof(ISoftDeletable).IsAssignableFrom(clr))
            {
                builder.HasQueryFilter(SoftDeleteFilter, BuildSoftDeleteFilter(clr));
            }

            if (IsAggregateRoot(clr))
            {
                // Optimistic concurrency on aggregates via the PostgreSQL system column xmin.
                builder.Property<uint>(ConcurrencyTokenProperty).IsRowVersion();
            }
        }
    }

    private LambdaExpression BuildTenantFilter(Type clr)
    {
        // e => this.IsSystemScope || e.WorkspaceId == this.CurrentWorkspaceId
        var e = Expression.Parameter(clr, "e");
        var context = Expression.Constant(this);
        var system = Expression.Property(context, nameof(IsSystemScope));
        var workspace = Expression.Convert(Expression.Property(e, nameof(ITenantOwned.WorkspaceId)), typeof(Guid?));
        var current = Expression.Property(context, nameof(CurrentWorkspaceId));
        return Expression.Lambda(Expression.OrElse(system, Expression.Equal(workspace, current)), e);
    }

    private static LambdaExpression BuildSoftDeleteFilter(Type clr)
    {
        var e = Expression.Parameter(clr, "e");
        var deletedAt = Expression.Property(e, nameof(ISoftDeletable.DeletedAt));
        return Expression.Lambda(Expression.Equal(deletedAt, Expression.Constant(null, typeof(DateTimeOffset?))), e);
    }

    private static bool IsAggregateRoot(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
        {
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(AggregateRoot<>))
            {
                return true;
            }
        }

        return false;
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        BeforeSave();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        BeforeSave();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void BeforeSave()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified)
            {
                if (entry.Metadata.FindProperty(nameof(Entity<Guid>.CreatedAt)) is not null && entry.State == EntityState.Added)
                {
                    var created = entry.Property(nameof(Entity<Guid>.CreatedAt));
                    if (created.CurrentValue is DateTimeOffset value && value == default)
                    {
                        created.CurrentValue = now;
                    }
                }

                if (entry.Metadata.FindProperty(nameof(Entity<Guid>.UpdatedAt)) is not null)
                {
                    entry.Property(nameof(Entity<Guid>.UpdatedAt)).CurrentValue = now;
                }
            }

            // Tenant write guard: a write of tenant-owned data must match the current tenant (or run in a system scope).
            if (entry.Entity is ITenantOwned owned && entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && !Tenant.IsSystem)
            {
                if (Tenant.WorkspaceId is not { } current || owned.WorkspaceId != current)
                {
                    throw new CrossTenantWriteException(entry.Metadata.ClrType.Name, owned.WorkspaceId, Tenant.WorkspaceId);
                }
            }
        }
    }

    /// <summary>
    /// Adds an integration event to the outbox. It is written by the next <see cref="DbContext.SaveChangesAsync(CancellationToken)"/>,
    /// in the same transaction as the business change, and relayed to Kafka afterwards.
    /// </summary>
    public OutboxMessage Publish<TEvent>(TEvent integrationEvent, string key, Guid? workspaceId = null, Guid? correlationId = null, Guid? causationId = null)
        where TEvent : IIntegrationEvent
    {
        var attribute = Topics.Describe(typeof(TEvent));
        var now = DateTimeOffset.UtcNow;
        var envelope = Messaging.EventEnvelopes.Create(integrationEvent, workspaceId ?? Tenant.WorkspaceId, correlationId, causationId, now);

        var message = new OutboxMessage
        {
            Id = envelope.MessageId,
            Topic = attribute.Topic,
            Key = key,
            Type = envelope.Type,
            Envelope = JsonSerializer.SerializeToDocument(envelope, EnvelopeJson.Options),
            OccurredAt = now,
            Shard = OutboxMessage.ShardOf(key),
            Headers = Messaging.KafkaProducer.HeadersFor(envelope),
        };

        OutboxMessages.Add(message);
        return message;
    }

    /// <summary>True if the entity type has the tenant filter. Used by architecture tests.</summary>
    public static bool HasTenantFilter(IEntityType entityType) =>
        entityType.GetDeclaredQueryFilters().Any(f => f.Key == TenantFilter);
}

/// <summary>Ambient correlation for the message being handled, so events produced while handling it are chained.</summary>
public static class MessagingContext
{
    private static readonly AsyncLocal<(Guid MessageId, Guid CorrelationId)?> Current = new();

    public static Guid? CurrentMessageId => Current.Value?.MessageId;

    public static Guid? CurrentCorrelationId => Current.Value?.CorrelationId;

    public static IDisposable Begin(Guid messageId, Guid correlationId)
    {
        var previous = Current.Value;
        Current.Value = (messageId, correlationId);
        return new Restore(previous);
    }

    private sealed class Restore((Guid, Guid)? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

public sealed class CrossTenantWriteException(string entity, Guid entityWorkspace, Guid? currentWorkspace)
    : InvalidOperationException($"Write to {entity} of workspace {entityWorkspace} rejected in tenant scope {currentWorkspace?.ToString() ?? "none"}.");
