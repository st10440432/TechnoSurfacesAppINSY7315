using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Services;

/// <summary>
/// The person signed in, as the page chrome shows them: name, initials and role.
/// The role comes from the Identity role claim, which is what the authorisation
/// policies check; the name comes from the full-name claim the session carries, or
/// from the domain user, which shares the Identity id, for an older session.
/// Worked out once per request.
/// </summary>
public sealed class SignedInUser
{
    private readonly IHttpContextAccessor _http;
    private readonly TechnoSurfacesDbContext _db;
    private Profile? _profile;

    public SignedInUser(IHttpContextAccessor http, TechnoSurfacesDbContext db)
    {
        _http = http;
        _db = db;
    }

    private ClaimsPrincipal? Principal => _http.HttpContext?.User;

    public bool IsSignedIn => Principal?.Identity?.IsAuthenticated == true;

    public string Id => Principal?.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";

    public bool IsManagingDirector => Principal?.IsInRole(Roles.ManagingDirector) == true;

    public async Task<Profile> GetAsync()
    {
        if (_profile is not null)
            return _profile;

        var email = Principal?.Identity?.Name ?? "";
        var role = IsManagingDirector ? "Managing Director" : "Estimator";

        // The name travels in the session (AppClaimsPrincipalFactory), so the side
        // menu on every page needs no query for it (NFR-05).
        if (Principal?.FindFirstValue(AppClaimsPrincipalFactory.FullNameClaim) is { Length: > 0 } fullName)
            return _profile = new Profile(fullName, email, Initials(fullName), role);

        // A session issued before the name was carried in it, until the security
        // stamp check rebuilds it within a minute.
        var user = string.IsNullOrEmpty(Id)
            ? null
            : await _db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == Id);

        var name = string.IsNullOrWhiteSpace(user?.FullName) ? email : user!.FullName;
        _profile = new Profile(name, user?.Email ?? email, Initials(name), role);
        return _profile;
    }

    private static string Initials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpperInvariant(),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpperInvariant()
        };
    }

    public sealed record Profile(string FullName, string Email, string Initials, string RoleLabel);
}
