using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pishkhan.Infrastructure.Application;
using Pishkhan.Infrastructure.Persistence;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Identity.Contracts;
using Pishkhan.Modules.Identity.Features.Auth;
using Pishkhan.Modules.Identity.Features.Me;
using Pishkhan.Modules.Identity.Persistence;
using Pishkhan.Modules.Identity.Services;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Identity;

public static class IdentityModule
{
    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdentityOptions>().Bind(configuration.GetSection(IdentityOptions.Section));
        services.AddOptions<AltchaOptions>().Bind(configuration.GetSection(AltchaOptions.Section));
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
        services.AddScoped<SessionService>();
        services.AddScoped<OtpService>();
        services.AddSingleton<PhoneProtection>();
        services.AddSingleton<AltchaVerifier>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddHandlersFrom(typeof(IdentityModule).Assembly);
        return services;
    }

    /// <summary>Only <see cref="IUserDirectory"/> (AdminApi host, which serves no user endpoints).</summary>
    public static IServiceCollection AddIdentityDirectory(this IServiceCollection services)
    {
        services.AddModuleDbContext<IdentityDbContext>(IdentityDbContext.SchemaName);
        services.AddSingleton<PhoneProtection>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        var anonymous = app.MapPanelAnonymousApi().WithTags("Auth");

        anonymous.MapPost("/auth/otp/request", (RequestOtpRequest request, ICommandHandler<RequestOtpRequest, RequestOtpResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Accepted(value: r)))
            .RequireRateLimiting(RateLimitPolicies.Public)
            .WithSummary("Send a login code by SMS")
            .WithDescription("Rate limited per phone (5/hour), per IP (20/hour) and with a 60 s resend cooldown. Requires an ALTCHA payload unless disabled.")
            .Produces<RequestOtpResponse>(StatusCodes.Status202Accepted)
            .WithStandardProblems()
            .WithExamples(new RequestOtpRequest("۰۹۱۲۰۰۰۱۲۳۴", "eyJhbGdvcml0aG0iOiJTSEEtMjU2In0="), new RequestOtpResponse(120, 60));

        anonymous.MapPost("/auth/otp/verify", (VerifyOtpRequest request, ICommandHandler<VerifyOtpRequest, LoginResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Ok(r)))
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Verify the login code and start a session")
            .Produces<LoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithStandardProblems()
            .WithExamples(new VerifyOtpRequest("09120001234", "۱۲۳۴۵۶"));

        anonymous.MapPost("/auth/password/login", (PasswordLoginRequest request, ICommandHandler<PasswordLoginRequest, LoginResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Ok(r)))
            .RequireRateLimiting(RateLimitPolicies.Auth)
            .WithSummary("Sign in with phone and password (and TOTP when enabled)")
            .Produces<LoginResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithStandardProblems();

        anonymous.MapPost("/auth/token/refresh", (RefreshRequest request, ICommandHandler<RefreshRequest, TokenResponse> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(request, ct), r => Results.Ok(r)))
            .RequireRateLimiting(RateLimitPolicies.Public)
            .WithSummary("Rotate the refresh token")
            .WithDescription("Every call returns a new refresh token. Reusing an old one revokes the whole session.")
            .Produces<TokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .WithStandardProblems();

        var panel = app.MapPanelApi().WithTags("Account");

        panel.MapPost("/auth/logout", (ICommandHandler<LogoutCommand, Unit> handler, CancellationToken ct) =>
                Send(handler.HandleAsync(new LogoutCommand(), ct), _ => Results.NoContent()))
            .WithSummary("End the current session")
            .Produces(StatusCodes.Status204NoContent);

        panel.MapPost("/auth/realtime-token", (SessionService sessions, ICurrentUser caller) =>
            {
                var token = sessions.IssueRealtimeToken(caller.UserId, caller.SessionId);
                return Results.Ok(new RealtimeTokenResponse(token.Token, token.ExpiresAt));
            })
            .WithSummary("Issue a 5-minute token for the SignalR hub")
            .Produces<RealtimeTokenResponse>();

        panel.MapGet("/me", (IQueryHandler<GetMeQuery, MeResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(new GetMeQuery(), ct), r => Results.Ok(r)))
            .WithSummary("Current user")
            .Produces<MeResponse>()
            .WithExamples(null, new MeResponse(Guid.Parse("0199e5a3-7c1b-7a3e-9c1f-2b6f8d0e4a11"), "مریم احمدی", "09120001234", false, false, new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));

        panel.MapPatch("/me", (UpdateMeRequest request, ICommandHandler<UpdateMeRequest, MeResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct), r => Results.Ok(r)))
            .WithSummary("Update the profile")
            .Produces<MeResponse>()
            .WithStandardProblems()
            .WithExamples(new UpdateMeRequest("مریم احمدی"));

        panel.MapPut("/me/password", (SetPasswordRequest request, ICommandHandler<SetPasswordRequest, Unit> handler, CancellationToken ct) => Send(handler.HandleAsync(request, ct), _ => Results.NoContent()))
            .WithSummary("Set or change the optional password")
            .Produces(StatusCodes.Status204NoContent)
            .WithStandardProblems();

        panel.MapGet("/me/sessions", (IQueryHandler<ListSessionsQuery, IReadOnlyList<SessionResponse>> handler, CancellationToken ct) => Send(handler.HandleAsync(new ListSessionsQuery(), ct), r => Results.Ok(r)))
            .WithSummary("Active sessions")
            .Produces<IReadOnlyList<SessionResponse>>();

        panel.MapDelete("/me/sessions/{sessionId:guid}", (Guid sessionId, ICommandHandler<RevokeSessionCommand, Unit> handler, CancellationToken ct) => Send(handler.HandleAsync(new RevokeSessionCommand(sessionId), ct), _ => Results.NoContent()))
            .WithSummary("Revoke a session")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        panel.MapPost("/auth/totp/setup", (ICommandHandler<BeginTotpSetupCommand, TotpSetupResponse> handler, CancellationToken ct) => Send(handler.HandleAsync(new BeginTotpSetupCommand(), ct), r => Results.Ok(r)))
            .WithSummary("Start TOTP setup; returns the secret for the authenticator app")
            .Produces<TotpSetupResponse>();

        panel.MapPost("/auth/totp/enable", (TotpCodeRequest request, ICommandHandler<EnableTotpCommand, Unit> handler, CancellationToken ct) => Send(handler.HandleAsync(new EnableTotpCommand(request.Code), ct), _ => Results.NoContent()))
            .WithSummary("Confirm TOTP setup with a code")
            .Produces(StatusCodes.Status204NoContent)
            .WithStandardProblems();

        panel.MapPost("/auth/totp/disable", (TotpCodeRequest request, ICommandHandler<DisableTotpCommand, Unit> handler, CancellationToken ct) => Send(handler.HandleAsync(new DisableTotpCommand(request.Code), ct), _ => Results.NoContent()))
            .WithSummary("Turn TOTP off (requires a current code)")
            .Produces(StatusCodes.Status204NoContent)
            .WithStandardProblems();

        app.MapPublicApi().WithTags("Bot protection").MapGet("/altcha/challenge", (AltchaVerifier altcha) => Results.Ok(altcha.CreateChallenge()))
            .WithSummary("ALTCHA proof-of-work challenge for sign-up and forms")
            .Produces<AltchaChallenge>();

        return app;
    }

    private static async Task<IResult> Send<T>(Task<SharedKernel.Result<T>> task, Func<T, IResult> onSuccess) => (await task).ToHttp(onSuccess);
}
