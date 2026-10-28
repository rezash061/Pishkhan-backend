using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Pishkhan.Web.Auth;

public static class AuthSchemes
{
    /// <summary>Sellers and their team (Api host).</summary>
    public const string User = "user";

    /// <summary>Back-office staff (AdminApi host). Separate key and audience: user tokens are rejected and vice versa.</summary>
    public const string Staff = "staff";

    /// <summary>Hub-scoped short-lived token for SignalR (Api host).</summary>
    public const string Realtime = "realtime";
}

public static class PishkhanClaims
{
    public const string Subject = JwtRegisteredClaimNames.Sub;
    public const string SessionId = "sid";
    public const string WorkspacesHash = "ws";
    public const string AuthMethods = "amr";
    public const string Actor = "act";
    public const string Roles = "roles";
    public const string TokenUse = "token_use";
}

public static class AuthPolicies
{
    public const string User = "user";
    public const string Staff = "staff";
    public const string Realtime = "realtime";

    public static string StaffRole(string role) => "staff-role:" + role;
}

public static class AuthSetup
{
    /// <summary>Configures JWT bearer validation for the given scheme(s). Tokens of other audiences fail validation.</summary>
    public static IServiceCollection AddPishkhanAuthentication(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment, params string[] schemes)
    {
        var auth = services.AddAuthentication(schemes[0]);
        foreach (var scheme in schemes)
        {
            var options = configuration.GetSection($"Auth:{Section(scheme)}").Get<JwtAudienceOptions>() ?? new JwtAudienceOptions();
            if (scheme == AuthSchemes.Staff && configuration.GetSection("Auth:Staff").Get<JwtAudienceOptions>() is null)
            {
                options.Audience = "pishkhan-admin";
                options.Issuer = "https://admin.pishkhan.ir";
            }

            var ring = new JwtKeyRing(Section(scheme), options, environment, configuration);
            services.AddKeyedSingleton(scheme, ring);
            auth.AddJwtBearer(scheme, o =>
            {
                o.MapInboundClaims = false;
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudience = scheme == AuthSchemes.Realtime ? options.Audience + "-realtime" : options.Audience,
                    IssuerSigningKeys = ring.ValidationKeys,
                    ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = PishkhanClaims.Subject,
                    RoleClaimType = PishkhanClaims.Roles,
                };
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = ctx =>
                    {
                        // SignalR sends the realtime token in the query string for WebSockets.
                        if (scheme == AuthSchemes.Realtime && ctx.Request.Path.StartsWithSegments("/hubs", StringComparison.Ordinal) && ctx.Request.Query.TryGetValue("access_token", out var token))
                        {
                            ctx.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async ctx =>
                    {
                        var revocations = ctx.HttpContext.RequestServices.GetRequiredService<ISessionRevocationStore>();
                        var sid = ctx.Principal?.FindFirstValue(PishkhanClaims.SessionId);
                        if (sid is null || await revocations.IsRevokedAsync(scheme, sid, ctx.HttpContext.RequestAborted))
                        {
                            ctx.Fail("session_revoked");
                        }
                    },
                };
            });
        }

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.User, p => p.AddAuthenticationSchemes(AuthSchemes.User).RequireAuthenticatedUser().RequireClaim(PishkhanClaims.TokenUse, "access"))
            .AddPolicy(AuthPolicies.Staff, p => p.AddAuthenticationSchemes(AuthSchemes.Staff).RequireAuthenticatedUser().RequireClaim(PishkhanClaims.TokenUse, "access"))
            .AddPolicy(AuthPolicies.Realtime, p => p.AddAuthenticationSchemes(AuthSchemes.Realtime).RequireAuthenticatedUser().RequireClaim(PishkhanClaims.TokenUse, "realtime"));

        foreach (var role in StaffRoles.All)
        {
            services.AddAuthorizationBuilder().AddPolicy(AuthPolicies.StaffRole(role), p => p
                .AddAuthenticationSchemes(AuthSchemes.Staff)
                .RequireAuthenticatedUser()
                .RequireClaim(PishkhanClaims.TokenUse, "access")
                .RequireAssertion(c => c.User.HasClaim(PishkhanClaims.Roles, role) || c.User.HasClaim(PishkhanClaims.Roles, StaffRoles.SuperAdmin)));
        }

        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, HttpCurrentUser>();
        services.TryAddSingleton<ISessionRevocationStore, RedisSessionRevocationStore>();
        services.TryAddSingleton<TokenFactory>();
        return services;
    }

    private static string Section(string scheme) => scheme switch
    {
        AuthSchemes.Staff => "Staff",
        _ => "User",
    };

    /// <summary>Publishes the active public keys of this host's schemes for internal validation (backend plan 3.4).</summary>
    public static IEndpointRouteBuilder MapJwks(this IEndpointRouteBuilder app, string scheme)
    {
        app.MapGet("/.well-known/jwks.json", (IServiceProvider sp) => Results.Json(new { keys = new[] { sp.GetRequiredKeyedService<JwtKeyRing>(scheme).ToJwk() } }))
            .ExcludeFromDescription()
            .AllowAnonymous();
        return app;
    }
}

public static class StaffRoles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string Support = "Support";
    public const string Finance = "Finance";
    public const string ContentEditor = "ContentEditor";
    public const string AiOps = "AIOps";
    public const string ReadOnly = "ReadOnly";

    public static readonly IReadOnlyList<string> All = [SuperAdmin, Support, Finance, ContentEditor, AiOps, ReadOnly];
}
