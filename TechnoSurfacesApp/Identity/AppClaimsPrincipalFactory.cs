using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TechnoSurfacesApp.Identity;

/// <summary>
/// Adds a claim to the session of an account still on a temporary password, so the
/// MustChangePasswordFilter can hold it on the change-password page (Task 1 8.2).
/// </summary>
public sealed class AppClaimsPrincipalFactory : UserClaimsPrincipalFactory<UserAccount, IdentityRole>
{
    public const string MustChangePasswordClaim = "ts:must_change_password";

    public AppClaimsPrincipalFactory(
        UserManager<UserAccount> userManager,
        RoleManager<IdentityRole> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options) { }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(UserAccount user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        if (user.MustChangePassword)
            identity.AddClaim(new Claim(MustChangePasswordClaim, "true"));
        return identity;
    }
}