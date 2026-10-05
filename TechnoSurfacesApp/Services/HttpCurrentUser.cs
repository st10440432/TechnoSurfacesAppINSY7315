using System.Security.Claims;
using TechnoSurfaces.Application.Auditing;

namespace TechnoSurfacesApp.Services;

/// <summary>
/// The signed-in user for the audit trail. Uses the Identity user id, which the
/// domain AppUser shares, so an audit row resolves to a named person.
/// </summary>
public sealed class HttpCurrentUser : ICurrentUser
{
    private readonly IHttpContextAccessor _http;

    public HttpCurrentUser(IHttpContextAccessor http) => _http = http;

    public string UserId =>
        _http.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ICurrentUser.SystemUserId;
}