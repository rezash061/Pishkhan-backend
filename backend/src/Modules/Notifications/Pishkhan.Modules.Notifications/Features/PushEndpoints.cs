using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Pishkhan.Infrastructure.Security;
using Pishkhan.Modules.Notifications.Domain;
using Pishkhan.Modules.Notifications.Persistence;
using Pishkhan.Modules.Notifications.Services;
using Pishkhan.SharedKernel;
using Pishkhan.Web.Auth;
using Pishkhan.Web.Http;
using Pishkhan.Web.OpenApi;

namespace Pishkhan.Modules.Notifications.Features;

/// <summary>The VAPID public key for <c>pushManager.subscribe({ applicationServerKey })</c>.</summary>
public sealed record PushKeyResponse(string PublicKey);

public sealed record PushKeys(string P256dh, string Auth);

/// <summary>The browser's <c>PushSubscription.toJSON()</c>: <c>endpoint</c> and <c>keys</c>.</summary>
public sealed record PushSubscriptionRequest(string Endpoint, PushKeys Keys);

public sealed record PushSubscriptionResponse(Guid Id, string Endpoint, string? UserAgent, DateTimeOffset CreatedAt, DateTimeOffset? LastDeliveredAt);

/// <summary>
/// Web push subscriptions of the signed-in user (backend plan 9.15). Push follows each kind's <c>push</c> preference per workspace; it is
/// unreliable in Iran, so in-app stays on.
/// </summary>
internal static class PushEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var me = app.MapPanelApi().MapGroup("/me").WithTags("Notifications");

        me.MapGet("/push/key", (IOptions<WebPushOptions> options) =>
                options.Value.Enabled ? Results.Ok(new PushKeyResponse(options.Value.PublicKey!)) : Error.NotFound("push.unavailable").ToProblem())
            .WithSummary("The VAPID public key for subscribing this browser")
            .WithDescription("404 `push.unavailable` when web push is not configured on the server.")
            .Produces<PushKeyResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        me.MapGet("/push-subscriptions", ListAsync)
            .WithSummary("The caller's subscribed browsers")
            .Produces<IReadOnlyList<PushSubscriptionResponse>>();

        me.MapPut("/push-subscriptions", SubscribeAsync)
            .WithSummary("Subscribe this browser (send it again whenever the browser renews it)")
            .WithDescription("The endpoint must be https on a known push service (400 `validation.push.endpoint`). The same endpoint again updates it. At most 10 browsers per user; the oldest is replaced.")
            .Produces<PushSubscriptionResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithStandardProblems();

        me.MapDelete("/push-subscriptions", UnsubscribeAsync)
            .WithSummary("Unsubscribe a browser by its endpoint (on sign-out or when the user turns push off)")
            .Produces(StatusCodes.Status204NoContent);
    }

    private static PushSubscriptionResponse ToResponse(PushSubscription s) => new(s.Id, s.Endpoint, s.UserAgent, s.CreatedAt, s.LastDeliveredAt);

    private static async Task<IResult> ListAsync(NotificationsDbContext db, ICurrentUser caller, CancellationToken ct) =>
        Results.Ok((await db.PushSubscriptions.AsNoTracking().Where(s => s.UserId == caller.UserId).OrderByDescending(s => s.CreatedAt).ToListAsync(ct)).Select(ToResponse).ToList());

    private static async Task<IResult> SubscribeAsync(
        PushSubscriptionRequest request, NotificationsDbContext db, ICurrentUser caller, IOptions<WebPushOptions> options, IClock clock, CancellationToken ct)
    {
        var o = options.Value;
        if (!o.Enabled)
        {
            return Error.NotFound("push.unavailable").ToProblem();
        }

        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (request.Endpoint is not { Length: > 0 and <= PushSubscription.MaxEndpoint } endpoint || !Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || !Allowed(uri, o))
        {
            errors["endpoint"] = ["validation.push.endpoint"];
        }

        if (!ValidKey(request.Keys?.P256dh, 65, p => p[0] == 0x04))
        {
            errors["keys.p256dh"] = ["validation.push.key"];
        }

        if (!ValidKey(request.Keys?.Auth, 16, _ => true))
        {
            errors["keys.auth"] = ["validation.push.key"];
        }

        if (errors.Count > 0)
        {
            return Error.Validation("validation.failed", errors).ToProblem();
        }

        var now = clock.UtcNow;
        var userAgent = caller.UserAgent is { Length: > 300 } ua ? ua[..300] : caller.UserAgent;
        var subscription = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == request.Endpoint, ct);
        if (subscription is null)
        {
            var mine = await db.PushSubscriptions.Where(s => s.UserId == caller.UserId).OrderBy(s => s.CreatedAt).ToListAsync(ct);
            db.PushSubscriptions.RemoveRange(mine.Take(Math.Max(0, mine.Count - PushSubscription.MaxPerUser + 1)));
            subscription = PushSubscription.Create(caller.UserId, request.Endpoint, request.Keys!.P256dh, request.Keys.Auth, userAgent, now);
            db.PushSubscriptions.Add(subscription);
        }
        else
        {
            subscription.Renew(caller.UserId, request.Keys!.P256dh, request.Keys.Auth, userAgent);
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The same browser subscribed twice at once: the other request saved it.
            return Error.Conflict("push.subscription_busy").ToProblem();
        }

        return Results.Ok(ToResponse(subscription));
    }

    private static async Task<IResult> UnsubscribeAsync(string? endpoint, NotificationsDbContext db, ICurrentUser caller, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(endpoint))
        {
            await db.PushSubscriptions.Where(s => s.Endpoint == endpoint && s.UserId == caller.UserId).ExecuteDeleteAsync(ct);
        }

        return Results.NoContent();
    }

    private static bool Allowed(Uri uri, WebPushOptions o)
    {
        try
        {
            SafeHttpClient.EnsureAllowed(uri, o.AllowedHosts);
            return true;
        }
        catch (SafeHttpException)
        {
            return false;
        }
    }

    private static bool ValidKey(string? value, int length, Func<byte[], bool> check)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 100)
        {
            return false;
        }

        try
        {
            var bytes = Base64Url.Decode(value);
            return bytes.Length == length && check(bytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
