namespace Pishkhan.Modules.Backoffice.Contracts;

/// <summary>Creates staff accounts from trusted tooling (the Migrator's bootstrap command).</summary>
public interface IStaffProvisioning
{
    Task<Guid> CreateAsync(string username, string password, IReadOnlyList<string> roles, CancellationToken cancellationToken);
}
