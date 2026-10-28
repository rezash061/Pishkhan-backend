using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Pishkhan.SharedKernel;

namespace Pishkhan.Web.Auth;

public sealed record IssuedToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Creates ES256 access tokens for one audience.</summary>
public sealed class TokenFactory(IServiceProvider services, IClock clock)
{
    private static readonly JsonWebTokenHandler Handler = new() { SetDefaultTimesOnTokenCreation = false };

    public IssuedToken CreateAccessToken(string scheme, Guid subject, Guid sessionId, IEnumerable<Claim> extraClaims, TimeSpan? lifetime = null, string tokenUse = "access")
    {
        var ring = services.GetRequiredKeyedService<JwtKeyRing>(scheme);
        var now = clock.UtcNow;
        var expires = now + (lifetime ?? ring.Options.AccessTokenLifetime);
        var claims = new List<Claim>
        {
            new(PishkhanClaims.Subject, subject.ToString()),
            new(PishkhanClaims.SessionId, sessionId.ToString()),
            new(PishkhanClaims.TokenUse, tokenUse),
            new(JwtRegisteredClaimNames.Jti, Uuid.NewV7().ToString()),
        };
        claims.AddRange(extraClaims);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = ring.Options.Issuer,
            Audience = tokenUse == "realtime" ? ring.Options.Audience + "-realtime" : ring.Options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = ring.SigningCredentials,
        };
        return new IssuedToken(Handler.CreateToken(descriptor), expires);
    }

    public TimeSpan AccessTokenLifetime(string scheme) => services.GetRequiredKeyedService<JwtKeyRing>(scheme).Options.AccessTokenLifetime;
}
