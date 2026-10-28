using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Pishkhan.Web.Auth;

public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid UserId { get; }

    Guid SessionId { get; }

    /// <summary>Set when a staff member is impersonating this user (the <c>act</c> claim).</summary>
    Guid? ActorStaffId { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }
}

public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid UserId => Guid.TryParse(Principal?.FindFirstValue(PishkhanClaims.Subject), out var id) ? id : throw new InvalidOperationException("No authenticated user.");

    public Guid SessionId => Guid.TryParse(Principal?.FindFirstValue(PishkhanClaims.SessionId), out var id) ? id : Guid.Empty;

    public Guid? ActorStaffId => Guid.TryParse(Principal?.FindFirstValue(PishkhanClaims.Actor), out var id) ? id : null;

    public string? IpAddress => accessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent
    {
        get
        {
            var ua = accessor.HttpContext?.Request.Headers.UserAgent.ToString();
            return string.IsNullOrEmpty(ua) ? null : ua[..Math.Min(ua.Length, 300)];
        }
    }
}
