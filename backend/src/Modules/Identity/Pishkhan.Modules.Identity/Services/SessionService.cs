using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Storage;
using Pishkhan.Modules.Identity.Domain;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Tenancy.Contracts;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;

namespace Pishkhan.Modules.Identity.Services;

public sealed record AuthTokens(string AccessToken, DateTimeOffset AccessTokenExpiresAt, string RefreshToken, DateTimeOffset RefreshTokenExpiresAt);

/// <summary>Starts, rotates and revokes refresh-token sessions and issues access tokens (backend plan 3.4).</summary>
internal sealed class SessionService(
    IdentityDbContext db,
    TokenFactory tokens,
    IMembershipQueries memberships,
    ISessionRevocationStore revocations,
    IClock clock,
    IOptions<IdentityOptions> options)
{
    public async Task<AuthTokens> StartAsync(User user, string amr, string? ip, string? userAgent, CancellationToken cancellationToken)
    {
        var o = options.Value;
        var now = clock.UtcNow;
        var refresh = NewRefreshToken();
        var session = Session.Start(user.Id, Hash(refresh), amr, o.RefreshTokenLifetime, o.SessionFamilyLifetime, ip, userAgent, now);
        db.Sessions.Add(session);
        user.RecordLogin(now);
        await db.SaveChangesAsync(cancellationToken);
        return await IssueAsync(session, refresh, cancellationToken);
    }

    public async Task<Result<AuthTokens>> RefreshAsync(string refreshToken, string? ip, string? userAgent, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var hash = Hash(refreshToken);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.RefreshHash == hash, cancellationToken);
        if (session is null)
        {
            return Error.Unauthorized("auth.refresh.invalid");
        }

        if (session.RevokedReason == Session.ReasonRotated)
        {
            // A rotated token was presented again: someone holds a stolen copy. Revoke the whole family.
            await RevokeFamilyAsync(session.UserId, session.FamilyId, Session.ReasonReuse, cancellationToken);
            return Error.Unauthorized("auth.refresh.reused");
        }

        if (!session.IsActive(now))
        {
            return Error.Unauthorized("auth.refresh.expired");
        }

        var user = await db.Users.FirstAsync(u => u.Id == session.UserId, cancellationToken);
        if (user.Status != UserStatus.Active)
        {
            return Error.Forbidden("auth.user.suspended");
        }

        var newRefresh = NewRefreshToken();
        var next = session.Rotate(Hash(newRefresh), options.Value.RefreshTokenLifetime, ip, userAgent, now);
        db.Sessions.Add(next);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // Two refreshes raced with the same token; the loser is treated as reuse.
            db.ChangeTracker.Clear();
            await RevokeFamilyAsync(session.UserId, session.FamilyId, Session.ReasonReuse, cancellationToken);
            return Error.Unauthorized("auth.refresh.reused");
        }

        return await IssueAsync(next, newRefresh, cancellationToken);
    }

    public async Task<bool> RevokeFamilyAsync(Guid userId, Guid familyId, string reason, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var rows = await db.Sessions.Where(s => s.UserId == userId && s.FamilyId == familyId && s.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.RevokedAt, now).SetProperty(x => x.RevokedReason, reason).SetProperty(x => x.UpdatedAt, now), cancellationToken);
        var exists = rows > 0 || await db.Sessions.AnyAsync(s => s.UserId == userId && s.FamilyId == familyId, cancellationToken);
        if (exists)
        {
            await revocations.RevokeAsync(AuthSchemes.User, familyId.ToString(), tokens.AccessTokenLifetime(AuthSchemes.User) + TimeSpan.FromMinutes(1), cancellationToken);
        }

        return exists;
    }

    public IssuedToken IssueRealtimeToken(Guid userId, Guid sessionId) =>
        tokens.CreateAccessToken(AuthSchemes.User, userId, sessionId, [], options.Value.RealtimeTokenLifetime, tokenUse: "realtime");

    private async Task<AuthTokens> IssueAsync(Session session, string refreshToken, CancellationToken cancellationToken)
    {
        var workspaces = await memberships.GetWorkspaceIdsAsync(session.UserId, cancellationToken);
        var wsHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.ASCII.GetBytes(string.Join(',', workspaces))))[..16];
        var access = tokens.CreateAccessToken(AuthSchemes.User, session.UserId, session.FamilyId,
        [
            new Claim(PishkhanClaims.WorkspacesHash, wsHash),
            new Claim(PishkhanClaims.AuthMethods, session.AuthMethods),
        ]);
        return new AuthTokens(access.Token, access.ExpiresAt, refreshToken, session.ExpiresAt);
    }

    private static string NewRefreshToken() => Base64Url.Encode(RandomNumberGenerator.GetBytes(32));

    /// <summary>Refresh tokens are 256-bit random values, so a fast hash is enough; only the hash is stored.</summary>
    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
