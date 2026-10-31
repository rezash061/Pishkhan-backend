using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.Modules.Backoffice.Persistence;

namespace Pishkhan.Modules.Backoffice.Features;

internal sealed class StaffDirectory(BackofficeDbContext db) : IStaffDirectory
{
    public Task<string?> GetUsernameAsync(Guid staffId, CancellationToken cancellationToken) =>
        db.StaffUsers.AsNoTracking().Where(s => s.Id == staffId).Select(s => (string?)s.Username).FirstOrDefaultAsync(cancellationToken);
}
