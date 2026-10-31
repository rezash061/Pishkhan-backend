using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Contracts;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Caching;
using Pishkhan.Modules.Audit.Contracts;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using StackExchange.Redis;

namespace Pishkhan.Modules.Identity.Features.Auth;

public sealed record ExchangeSupportSessionRequest(string Token);

/// <summary>
/// A 30-minute access token for <c>workspaceId</c> as <c>userId</c>; there is no refresh token. Show a support-session banner with
/// <c>staffUsername</c> while it lasts; <c>POST /auth/logout</c> ends it.
/// </summary>
public sealed record SupportSessionResponse(string AccessToken, DateTimeOffset AccessTokenExpiresAt, Guid WorkspaceId, Guid UserId, string StaffUsername, string Reason);

/// <summary>
/// Step 2 of a support session: the user panel exchanges the one-time token from the back office. The member must still belong to
/// the workspace (and the workspace must not be pending deletion). Audited in the workspace, and the owner is notified, without the
/// staff member's name (the workspace audit view shows staff as «support»).
/// </summary>
internal sealed class ExchangeSupportSessionHandler(
    IdentityDbContext db,
    [FromKeyedServices(RedisInstances.State)] IConnectionMultiplexer redis,
    RedisKeys keys,
    IMembershipQueries memberships,
    IWorkspaceAccessResolver access,
    SessionService sessions,
    ICurrentUser caller,
    IClock clock) : ICommandHandler<ExchangeSupportSessionRequest, SupportSessionResponse>
{
    public async Task<Result<SupportSessionResponse>> HandleAsync(ExchangeSupportSessionRequest command, CancellationToken cancellationToken)
    {
        var (area, rest) = SupportSessions.GrantKey(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(command.Token ?? string.Empty))));
        var stored = await redis.GetDatabase().StringGetDeleteAsync(keys.For(area, rest));
        if (stored.IsNullOrEmpty || JsonSerializer.Deserialize<SupportSessionGrant>(stored.ToString(), EnvelopeJson.Options) is not { } grant)
        {
            return Error.Unauthorized("auth.support_session.invalid");
        }

        var owner = await memberships.GetOwnerIdAsync(grant.WorkspaceId, cancellationToken);
        var userId = grant.UserId ?? owner;
        if (owner is null || userId is not { } member || await access.ResolveAsync(member, grant.WorkspaceId, cancellationToken) is null)
        {
            return Error.NotFound();
        }

        if (!await db.Users.AnyAsync(u => u.Id == member && u.Status == UserStatus.Active, cancellationToken))
        {
            return Error.Forbidden("auth.user.suspended");
        }

        var now = clock.UtcNow;
        var sessionId = Uuid.NewV7(now);
        var token = await sessions.IssueSupportTokenAsync(member, sessionId, grant.StaffId, grant.StaffUsername, cancellationToken);

        var entry = AuditEntries.Create(
            AuditActor.User(member, grant.StaffId, caller.IpAddress), "support_session.start", "support_session", sessionId.ToString(), grant.WorkspaceId, now,
            after: new { expiresAt = token.ExpiresAt }, reason: grant.Reason);
        db.Publish(entry, entry.PartitionKey(), grant.WorkspaceId);
        db.Publish(
            new NotificationRequested(grant.WorkspaceId, owner, "support", "کارشناس پشتیبانی پیشخوان وارد پنل شما شد", grant.Reason, "/settings/audit"),
            grant.WorkspaceId.ToString(), grant.WorkspaceId);
        await db.SaveChangesAsync(cancellationToken);

        return new SupportSessionResponse(token.Token, token.ExpiresAt, grant.WorkspaceId, member, grant.StaffUsername, grant.Reason);
    }
}
