using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TechnoSurfaces.Infrastructure.Data;

namespace TechnoSurfacesApp.Identity;

/// <summary>
/// Adds two claims to the session. One marks an account still on a temporary
/// password, so the MustChangePasswordFilter can hold it on the change-password page
/// (Task 1 8.2). The other carries the person's full name, so the side menu on every
/// page can show it without looking it up (NFR-05).
///
/// The session is rebuilt here whenever the security stamp is checked, once a minute,
/// so a changed name reaches an open session within a minute.
/// </summary>
public sealed class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<UserAccount, IdentityRole>
{
    public const string MustChangePasswordClaim = "ts:must_change_password";

    /// <summary>The person's full name, from the domain user that shares the account's id.</summary>
    public const string FullNameClaim = "ts:full_name";

    private readonly TechnoSurfacesDbContext _db;

    public AppClaimsPrincipalFactory(
        UserManager<UserAccount> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options,
        TechnoSurfacesDbContext db)
        : base(userManager, roleManager, options) => _db = db;

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(UserAccount user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword)
            identity.AddClaim(new Claim(MustChangePasswordClaim, "true"));

        var fullName = await _db.Users.AsNoTracking()
            .Where(u => u.Id == user.Id)
            .Select(u => u.FullName)
            .FirstOrDefaultAsync();
        if (!string.IsNullOrWhiteSpace(fullName))
            identity.AddClaim(new Claim(FullNameClaim, fullName));

        return identity;
    }
}