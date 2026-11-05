namespace Pishkhan.Modules.Backoffice.Contracts;

/// <summary>Creates staff accounts from trusted tooling (the Migrator's bootstrap command).</summary>
public interface IStaffProvisioning
{
    Task<Guid> CreateAsync(string username, string password, IReadOnlyList<string> roles, CancellationToken cancellationToken);
}

/// <summary>In-process queries other modules may use (AdminApi host).</summary>
public interface IStaffDirectory
{
    /// <summary>The staff member's username, for audit entries, or null.</summary>
    Task<string?> GetUsernameAsync(Guid staffId, CancellationToken cancellationToken);
}

/// <summary>Feature flags set in the back office (AD-1.8): global switch, rollout share, plan list, per-workspace override.</summary>
public interface IFeatureFlags
{
    Task<bool> IsEnabledAsync(string key, Guid workspaceId, CancellationToken cancellationToken);

    /// <summary>Keys of the flags that are on for the workspace.</summary>
    Task<IReadOnlyList<string>> GetEnabledAsync(Guid workspaceId, CancellationToken cancellationToken);
}
