using System.Security.Claims;


namespace TechnoSurfacesApp.Services;


/// <summary>The result of a sign-in attempt, for the controller to turn into a message.</summary>
public enum SignInOutcome
{
    Succeeded,
    InvalidCredentials,
    LockedOut,
    Deactivated
}

/// <summary>
/// Signing in and out. The rules live behind this interface so the controller
/// holds no authentication logic and the rules can be tested on their own.
/// </summary>
public interface ISignInService
{
    Task<SignInOutcome> SignInAsync(string email, string password, bool rememberMe);
    Task SignOutAsync();

    /// <summary>Changes the signed-in user's password. Returns the errors; empty means it worked.</summary>
    Task<IReadOnlyList<string>> ChangePasswordAsync(ClaimsPrincipal principal, string currentPassword, string newPassword);
}