using TechnoSurfacesApp.Models;

namespace TechnoSurfacesApp.Services;

/// <summary>One account on the Users screen.</summary>
public sealed record UserRow(
    string Id, string FullName, string Email, UserRole Role,
    bool IsActive, bool IsLockedOut, bool MustChangePassword, DateTime CreatedOn)
{
    /// <summary>Not tracked yet; the screen shows "Never".</summary>
    public DateTime? LastLogin => null;

    public string RoleLabel => Role == UserRole.ManagingDirector ? "Managing Director" : "Estimator";

    public string Initials => string.Concat(
        FullName.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(p => p[0]))
        .ToUpperInvariant();
}

/// <summary>The outcome of an account change. A temporary password is shown once.</summary>
public sealed record UserAdminResult(bool Succeeded, string? Error = null, string? TemporaryPassword = null)
{
    public static UserAdminResult Ok(string? temporaryPassword = null) => new(true, null, temporaryPassword);
    public static UserAdminResult Fail(string error) => new(false, error);
}

/// <summary>
/// Account administration for the Managing Director (US-26). There is no
/// self-registration and no email: the MD creates each account and hands over a
/// temporary password, which must be changed at first sign-in (Task 1 8.2).
/// </summary>
public interface IUserAdminService
{
    Task<IReadOnlyList<UserRow>> ListAsync();
    Task<UserAdminResult> CreateAsync(string fullName, string email, string role);
    Task<UserAdminResult> SetActiveAsync(string userId, bool active);
    Task<UserAdminResult> ReissuePasswordAsync(string userId);
}