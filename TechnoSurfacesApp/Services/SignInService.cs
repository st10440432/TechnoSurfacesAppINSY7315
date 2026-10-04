using Microsoft.AspNetCore.Identity;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;
using System.Security.Claims;

namespace TechnoSurfacesApp.Services;

/// <summary>
/// Sign-in through ASP.NET Core Identity. Identity verifies the PBKDF2 password
/// hash and applies the lockout policy; this service adds the rule that a
/// deactivated account cannot sign in (US-26).
/// </summary>
public sealed class SignInService : ISignInService
{
    private readonly SignInManager<UserAccount> _signInManager;
    private readonly UserManager<UserAccount> _userManager;
    private readonly TechnoSurfacesDbContext _db;
    private readonly ILogger<SignInService> _logger;

    public SignInService(
        SignInManager<UserAccount> signInManager,
        UserManager<UserAccount> userManager,
        TechnoSurfacesDbContext db,
        ILogger<SignInService> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _db = db;
        _logger = logger;
    }

    public async Task<SignInOutcome> SignInAsync(string email, string password, bool rememberMe)
    {
        var account = await _userManager.FindByEmailAsync(email.Trim());
        if (account is null)
        {
            _logger.LogInformation("Sign-in failed: invalid credentials.");
            return SignInOutcome.InvalidCredentials;
        }

        // Verifies the password hash and counts failures towards lockout.
        // lockoutOnFailure: true is what makes the lockout policy take effect.
        var check = await _signInManager.CheckPasswordSignInAsync(account, password, lockoutOnFailure: true);

        if (check.IsLockedOut)
        {
            // No email address or name in the log (POPIA, Task 1 8.5).
            _logger.LogWarning("Sign-in refused: account is locked out.");
            return SignInOutcome.LockedOut;
        }

        if (!check.Succeeded)
        {
            _logger.LogInformation("Sign-in failed: invalid credentials.");
            return SignInOutcome.InvalidCredentials;
        }

        // US-26. Checked only once the password is proven, so the form never tells
        // someone without the password that the account exists but is deactivated.
        // An account with no domain user is refused, not allowed.
        var user = await _db.Users.FindAsync(account.Id);
        if (user is null || !user.IsActive)
        {
            _logger.LogWarning("Sign-in refused: account is deactivated.");
            return SignInOutcome.Deactivated;
        }

        await _signInManager.SignInAsync(account, rememberMe);
        return SignInOutcome.Succeeded;
    }

    public Task SignOutAsync() => _signInManager.SignOutAsync();

    public async Task<IReadOnlyList<string>> ChangePasswordAsync(
    ClaimsPrincipal principal, string currentPassword, string newPassword)
    {
        var account = await _userManager.GetUserAsync(principal);
        if (account is null)
            return new[] { "Your session has expired. Sign in again." };

        if (newPassword == currentPassword)
            return new[] { "Choose a password that is different from the temporary one." };

        // Checks the current password and the policy; updates the security stamp,
        // which invalidates every other session for this account.
        var changed = await _userManager.ChangePasswordAsync(account, currentPassword, newPassword);
        if (!changed.Succeeded)
            return changed.Errors.Select(e => e.Description).ToList();

        account.MustChangePassword = false;
        await _userManager.UpdateAsync(account);

        // Reissue this session without the must-change claim.
        await _signInManager.RefreshSignInAsync(account);

        _logger.LogInformation("Password changed by its owner.");
        return Array.Empty<string>();
    }

}