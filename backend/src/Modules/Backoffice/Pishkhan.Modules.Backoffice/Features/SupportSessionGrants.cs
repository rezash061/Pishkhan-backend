using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Backoffice.Persistence;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using StackExchange.Redis;

namespace Pishkhan.Modules.Backoffice.Features;

/// <summary><c>userId</c> defaults to the workspace owner. The reason is shown to the owner and kept in the audit log.</summary>
public sealed record StartSupportSessionRequest(string Reason, Guid? UserId);

public sealed record StartSupportSessionCommand(Guid WorkspaceId, StartSupportSessionRequest Request);

/// <summary>Open <c>url</c> in a new tab; it works once, until <c>expiresAt</c>. The token is never stored by the admin panel.</summary>
public sealed record SupportSessionLink(string Url, DateTimeOffset ExpiresAt);

internal sealed class StartSupportSessionValidator : AbstractValidator<StartSupportSessionCommand>
{
    public StartSupportSessionValidator() =>
        RuleFor(x => x.Request.Reason).NotEmpty().WithErrorCode("validation.required").MinimumLength(5).WithErrorCode("validation.too_short")
            .MaximumLength(300).WithErrorCode("validation.too_long");
}

/// <summary>
/// Step 1 of a support session (backend plan 3.4): the grant. The user panel exchanges the link's token (step 2, Api), which is where
/// the workspace and the member are checked.
/// </summary>
internal sealed class SupportSessionGrantHandler(
    BackofficeDbContext db,
    [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
    RedisKeys keys,
    ICurrentUser caller,
    IOptions<BackofficeOptions> options,
    IClock clock) : ICommandHandler<StartSupportSessionCommand, SupportSessionLink>
{
    public async Task<Result<SupportSessionLink>> HandleAsync(StartSupportSessionCommand command, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var username = await db.StaffUsers.AsNoTracking().Where(s => s.Id == caller.UserId).Select(s => s.Username).FirstOrDefaultAsync(cancellationToken) ?? "support";
        var reason = command.Request.Reason.Trim();
        var token = Base64Url.Encode(RandomNumberGenerator.GetBytes(32));
        var grant = new SupportSessionGrant(caller.UserId, username, command.WorkspaceId, command.Request.UserId, reason, now);
        var (area, rest) = SupportSessions.GrantKey(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token))));
        await redis.GetDatabase().StringSetAsync(keys.For(area, rest), JsonSerializer.Serialize(grant, EnvelopeJson.Options), SupportSessions.GrantLifetime);

        // A workspace-scoped entry, so the owner sees it in their audit log.
        var entry = AuditEntries.Create(
            AuditActor.Staff(caller.UserId, username, caller.IpAddress), "support_session.grant", "workspace", command.WorkspaceId.ToString(), command.WorkspaceId, now,
            after: new { userId = command.Request.UserId }, reason: reason);
        db.Publish(entry, entry.PartitionKey(), command.WorkspaceId);
        await db.SaveChangesAsync(cancellationToken);

        var url = $"{options.Value.UserPanelUrl.TrimEnd('/')}/api/auth/impersonation?token={token}";
        return new SupportSessionLink(url, now + SupportSessions.GrantLifetime);
    }
}
