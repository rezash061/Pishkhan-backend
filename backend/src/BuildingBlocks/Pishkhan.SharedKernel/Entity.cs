namespace Pishkhan.SharedKernel;

public interface IDomainEvent
{
    DateTimeOffset OccurredAt { get; }
}

/// <summary>Base for entities. Timestamps are set by the persistence interceptor.</summary>
public abstract class Entity<TId>
    where TId : struct
{
    public TId Id { get; protected init; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public abstract class AggregateRoot<TId> : Entity<TId>
    where TId : struct
{
    private readonly List<IDomainEvent> _domainEvents = [];

    public IReadOnlyList<IDomainEvent> DomainEvents => _domainEvents;

    protected void Raise(IDomainEvent domainEvent) => _domainEvents.Add(domainEvent);

    public void ClearDomainEvents() => _domainEvents.Clear();
}

/// <summary>Marks an entity owned by a workspace (tenant). The persistence layer adds a tenant query filter for every such entity.</summary>
public interface ITenantOwned
{
    Guid WorkspaceId { get; }
}

/// <summary>Marks an entity that is soft deleted.</summary>
public interface ISoftDeletable
{
    DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>Thrown when a domain invariant is violated. Never retried by consumers.</summary>
public class DomainException(string code, string? message = null) : Exception(message ?? code)
{
    public string Code { get; } = code;
}
