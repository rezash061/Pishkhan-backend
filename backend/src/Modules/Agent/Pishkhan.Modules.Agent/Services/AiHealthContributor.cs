using Microsoft.EntityFrameworkCore;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Modules.Agent.Contracts;
using Pishkhan.Modules.Agent.Persistence;
using Pishkhan.Modules.Backoffice.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Agent.Services;

/// <summary>
/// The back office health page's <c>ai</c> check: replies that failed in the last hour (the AI was unavailable) and providers whose last test
/// from the back office failed. Degraded, not unhealthy: a failed reply hands the conversation to a person.
/// </summary>
internal sealed class AiHealthContributor(AgentDbContext db, IClock clock) : IPlatformHealthContributor
{
    public async Task<IReadOnlyList<PlatformCheck>> CheckAsync(CancellationToken cancellationToken)
    {
        var from = clock.UtcNow.AddHours(-1);
        var traces = db.Traces.AsNoTracking().IgnoreQueryFilters([ModuleDbContext.TenantFilter]).Where(t => t.CreatedAt >= from);
        var total = await traces.CountAsync(cancellationToken);
        var failed = await traces.CountAsync(t => t.Outcome == AiReplyOutcomes.Failed, cancellationToken);
        var broken = await db.AiProviders.AsNoTracking().Where(p => p.Enabled && p.LastTestError != null).Select(p => p.Name).ToListAsync(cancellationToken);

        var status = failed >= 3 && failed * 5 >= total ? "Degraded" : broken.Count > 0 ? "Degraded" : "Healthy";
        var detail = total == 0 ? "no replies in the last hour" : $"{failed} of {total} replies failed in the last hour";
        if (broken.Count > 0)
        {
            detail += $"; last test failed: {string.Join(", ", broken)}";
        }

        return [new PlatformCheck("ai", status, detail)];
    }
}
