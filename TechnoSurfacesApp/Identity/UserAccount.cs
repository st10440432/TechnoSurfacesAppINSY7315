using Microsoft.AspNetCore.Identity;

namespace TechnoSurfacesApp.Identity;

/// <summary>
/// The credential record for a person who signs in. ASP.NET Core Identity owns the
/// password hash, lockout counters and security stamp.
///
/// This is deliberately not the domain AppUser. The domain user carries the role
/// and the active flag the business rules depend on; this class carries only what
/// authentication needs. The two share the same Id.
/// </summary>
public class UserAccount : IdentityUser
{
    /// <summary>
    /// The Managing Director sets each new user's first password, so the user must
    /// replace it at first sign-in (Task 1 8.2). Also set after a password re-issue.
    /// </summary>
    public bool MustChangePassword { get; set; } = true;
    public DateTime? LastSignInUtc { get; set; }
}
