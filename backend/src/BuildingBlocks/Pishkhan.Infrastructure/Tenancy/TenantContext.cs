namespace Pishkhan.Infrastructure.Tenancy;

/// <summary>
/// The workspace (tenant) the current request or message belongs to. Set from the route after the caller's membership is checked,
/// or from the event envelope in consumers. When no tenant is set, tenant-owned queries return nothing (fail closed).
/// </summary>
public interface ITenantContext
{
    Guid? WorkspaceId { get; }

    /// <summary>True inside <see cref="BeginSystemScope"/>: platform code that legitimately works across tenants (relays, ingest lookups).</summary>
    bool IsSystem { get; }

    void SetWorkspace(Guid workspaceId);

    IDisposable BeginSystemScope();
}

public sealed class TenantContext : ITenantContext
{
    private int _systemDepth;

    public Guid? WorkspaceId { get; private set; }

    public bool IsSystem => _systemDepth > 0;

    public void SetWorkspace(Guid workspaceId)
    {
        if (WorkspaceId is { } current && current != workspaceId)
        {
            throw new InvalidOperationException("The tenant of a scope cannot change once set.");
        }

        WorkspaceId = workspaceId;
    }

    public IDisposable BeginSystemScope()
    {
        _systemDepth++;
        return new Scope(this);
    }

    private sealed class Scope(TenantContext owner) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (!_disposed)
            {
                owner._systemDepth--;
                _disposed = true;
            }
        }
    }
}
