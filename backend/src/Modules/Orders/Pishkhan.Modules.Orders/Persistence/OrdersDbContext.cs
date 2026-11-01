using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Tenancy;
using Pishkhan.Modules.Orders.Domain;

namespace Pishkhan.Modules.Orders.Persistence;

/// <summary>The last order number given out in a workspace. Only touched through <see cref="OrdersDbContext.NextNumberAsync"/>.</summary>
public sealed class OrderCounter
{
    public Guid WorkspaceId { get; set; }

    public long LastNumber { get; set; }
}

public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options, ITenantContext tenant) : ModuleDbContext(options, tenant)
{
    public const string SchemaName = "orders";

    public override string Schema => SchemaName;

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderEvent> Events => Set<OrderEvent>();

    public DbSet<OrderCounter> Counters => Set<OrderCounter>();

    /// <summary>
    /// The workspace's next order number (1, 2, 3 ...), taken with one atomic upsert. A number taken by a request that then fails is
    /// skipped; numbers are unique, not gap-free.
    /// </summary>
    public async Task<long> NextNumberAsync(Guid workspaceId, CancellationToken ct) =>
        (await Database.SqlQuery<long>($"""
            INSERT INTO orders.counters (workspace_id, last_number) VALUES ({workspaceId}, 1)
            ON CONFLICT (workspace_id) DO UPDATE SET last_number = counters.last_number + 1
            RETURNING last_number AS "Value"
            """).ToListAsync(ct))[0];

    protected override void ConfigureModule(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>(b =>
        {
            b.ToTable("orders");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Source).HasMaxLength(20);
            b.Property(x => x.Status).HasMaxLength(20);
            b.Property(x => x.CustomerName).HasMaxLength(Order.MaxName);
            b.Property(x => x.PhoneEnc).HasMaxLength(1000);
            b.Property(x => x.PhoneBidx).HasMaxLength(100);
            b.Property(x => x.Note).HasMaxLength(Order.MaxNote);
            Json(b.Property(x => x.Items));
            Json(b.Property(x => x.Address));
            b.HasIndex(x => new { x.WorkspaceId, x.Number }).IsUnique();
            b.HasIndex(x => new { x.WorkspaceId, x.Status, x.CreatedAt });
            b.HasIndex(x => new { x.WorkspaceId, x.ContactId });
            b.HasIndex(x => new { x.WorkspaceId, x.ConversationId }).HasFilter("conversation_id IS NOT NULL");
            b.HasIndex(x => new { x.WorkspaceId, x.PhoneBidx }).HasFilter("phone_bidx IS NOT NULL");
            b.Ignore(x => x.DomainEvents);
            b.Ignore(x => x.TotalMoney);
            b.Ignore(x => x.IsEditable);
        });

        modelBuilder.Entity<OrderEvent>(b =>
        {
            b.ToTable("order_events");
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(20);
            b.Property(x => x.FromStatus).HasMaxLength(20);
            b.Property(x => x.ToStatus).HasMaxLength(20);
            b.Property(x => x.ActorType).HasMaxLength(20);
            b.Property(x => x.Note).HasMaxLength(Order.MaxNote);
            b.HasIndex(x => new { x.WorkspaceId, x.OrderId, x.CreatedAt });
        });

        modelBuilder.Entity<OrderCounter>(b =>
        {
            b.ToTable("counters");
            b.HasKey(x => x.WorkspaceId);
        });
    }

    private static void Json<T>(PropertyBuilder<T> property)
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

internal sealed class OrdersDbContextFactory : IDesignTimeDbContextFactory<OrdersDbContext>
{
    public OrdersDbContext CreateDbContext(string[] args) =>
        new(PersistenceExtensions.DesignTimeOptions<OrdersDbContext>(OrdersDbContext.SchemaName), new TenantContext());
}
