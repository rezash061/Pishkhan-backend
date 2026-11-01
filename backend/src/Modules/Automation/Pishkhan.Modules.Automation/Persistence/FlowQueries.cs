using Microsoft.EntityFrameworkCore;
using Pishkhan.Modules.Automation.Contracts;

namespace Pishkhan.Modules.Automation.Persistence;

internal sealed class FlowQueries(AutomationDbContext db) : IFlowQueries
{
    public Task<bool> FlowExistsAsync(Guid flowId, CancellationToken cancellationToken) =>
        db.Flows.AsNoTracking().AnyAsync(f => f.Id == flowId, cancellationToken);
}
