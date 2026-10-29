using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Messaging;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Audit.Persistence;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.SharedKernel;

namespace Pishkhan.Modules.Audit.Services;

/// <summary>
/// Platform group: stores audit entries. Idempotent by entry id (a redelivered entry hits the primary key and is skipped), so it
/// needs no inbox row. A user actor without a name gets their display name or masked phone.
/// </summary>
internal sealed class AuditSink(AuditDbContext db, IServiceProvider services, IClock clock) : IIntegrationEventHandler<AuditEntryRecorded>
{
    public async Task HandleAsync(AuditEntryRecorded message, EventEnvelope envelope, CancellationToken cancellationToken)
    {
        var name = await ActorNameAsync(message, cancellationToken);
        var before = message.Before?.GetRawText();
        var after = message.After?.GetRawText();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO audit.audit_entries (id, occurred_at, recorded_at, actor_type, actor_id, actor_name, on_behalf_of, workspace_id, action, entity_type, entity_id, reason, ip, before, after)
            VALUES ({message.EntryId}, {message.OccurredAt}, {clock.UtcNow}, {message.ActorType}, {message.ActorId}, {name}, {message.OnBehalfOf}, {message.WorkspaceId},
                    {message.Action}, {message.EntityType}, {message.EntityId}, {message.Reason}, {message.Ip}, {before}::jsonb, {after}::jsonb)
            ON CONFLICT (id, occurred_at) DO NOTHING
            """,
            cancellationToken);
    }

    private async Task<string> ActorNameAsync(AuditEntryRecorded message, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(message.ActorName))
        {
            return Clip(message.ActorName);
        }

        if (message.ActorType == AuditActorTypes.User && message.ActorId is { } userId && services.GetService<IUserDirectory>() is { } users)
        {
            var user = await users.GetAsync(userId, ct);
            if (user is not null)
            {
                return Clip(string.IsNullOrWhiteSpace(user.DisplayName) ? user.MaskedPhone : user.DisplayName);
            }
        }

        return message.ActorId is { } id ? $"{message.ActorType}:{id.ToString("N")[..8]}" : message.ActorType;
    }

    private static string Clip(string value) => value.Length <= 100 ? value : value[..100];
}
