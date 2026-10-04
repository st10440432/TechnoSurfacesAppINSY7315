using System.Security.Cryptography;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Models;
using DomainRole = TechnoSurfaces.Domain.UserRole;
using DomainUser = TechnoSurfaces.Domain.People.AppUser;

namespace TechnoSurfacesApp.Services;

/// <summary>
/// Managing Director only - enforced by the CanManageUsers policy on every action
/// that calls this service. Each person is an Identity account (credentials) and a
/// domain AppUser (name, role, active flag) sharing one Id.
/// </summary>
public sealed class UserAdminService : IUserAdminService
{
    private readonly UserManager<UserAccount> _accounts;
    private readonly TechnoSurfacesDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<UserAdminService> _logger;

    public UserAdminService(
        UserManager<UserAccount> accounts, TechnoSurfacesDbContext db,
        ICurrentUser currentUser, ILogger<UserAdminService> logger)
    {
        _accounts = accounts;
        _db = db;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<UserRow>> ListAsync()
    {
        var people = await _db.Users.AsNoTracking().ToListAsync();
        var accounts = await _accounts.Users.AsNoTracking().ToDictionaryAsync(a => a.Id);

        return people
            .Select(p =>
            {
                accounts.TryGetValue(p.Id, out var account);
                return new UserRow(
                    p.Id, p.FullName, p.Email ?? account?.Email ?? "",
                    p.Role == DomainRole.ManagingDirector ? UserRole.ManagingDirector : UserRole.Estimator,
                    p.IsActive,
                    account?.LockoutEnd > DateTimeOffset.UtcNow,
                    account?.MustChangePassword ?? false,
                    p.CreatedAtUtc);
            })
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.FullName)
            .ToList();
    }

    public async Task<UserAdminResult> CreateAsync(string fullName, string email, string role)
    {
        fullName = fullName.Trim();
        email = email.Trim();

        if (role is not (Roles.ManagingDirector or Roles.Estimator))
            return UserAdminResult.Fail("Choose a role.");

        if (await _accounts.FindByEmailAsync(email) is not null)
            return UserAdminResult.Fail("An account with that email address already exists.");

        var password = TemporaryPassword.Generate();
        var account = new UserAccount
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            MustChangePassword = true
        };

        var created = await _accounts.CreateAsync(account, password);
        if (!created.Succeeded)
            return UserAdminResult.Fail(string.Join(" ", created.Errors.Select(e => e.Description)));

        try
        {
            await _accounts.AddToRoleAsync(account, role);

            _db.Users.Add(new DomainUser
            {
                Id = account.Id,
                UserName = email,
                FullName = fullName,
                Email = email,
                Role = role == Roles.ManagingDirector ? DomainRole.ManagingDirector : DomainRole.Estimator,
                IsActive = true,
                CreatedAtUtc = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }
        catch
        {
            // Never leave a credential without its domain user.
            await _accounts.DeleteAsync(account);
            throw;
        }

        _logger.LogInformation("Account created by the Managing Director.");
        return UserAdminResult.Ok(password);
    }

    public async Task<UserAdminResult> SetActiveAsync(string userId, bool active)
    {
        if (!active && userId == _currentUser.UserId)
            return UserAdminResult.Fail("You cannot deactivate your own account.");

        var person = await _db.Users.FindAsync(userId);
        if (person is null)
            return UserAdminResult.Fail("That account does not exist.");

        person.IsActive = active;
        await _db.SaveChangesAsync();

        if (!active && await _accounts.FindByIdAsync(userId) is { } account)
        {
            // Ends any open session within a minute (security stamp validation).
            await _accounts.UpdateSecurityStampAsync(account);
        }

        return UserAdminResult.Ok();
    }

    public async Task<UserAdminResult> ReissuePasswordAsync(string userId)
    {
        var account = await _accounts.FindByIdAsync(userId);
        if (account is null)
            return UserAdminResult.Fail("That account does not exist.");

        var password = TemporaryPassword.Generate();
        var token = await _accounts.GeneratePasswordResetTokenAsync(account);
        var reset = await _accounts.ResetPasswordAsync(account, token, password);
        if (!reset.Succeeded)
            return UserAdminResult.Fail(string.Join(" ", reset.Errors.Select(e => e.Description)));

        // Must be changed at next sign-in; a locked-out user is let back in.
        account.MustChangePassword = true;
        await _accounts.SetLockoutEndDateAsync(account, null);
        await _accounts.ResetAccessFailedCountAsync(account);

        _logger.LogInformation("Temporary password re-issued by the Managing Director.");
        return UserAdminResult.Ok(password);
    }

    /// <summary>
    /// A random 16-character password that meets the policy. Letters and digits
    /// that are easily misread (I, l, O, 0, 1) are left out, because it is read
    /// out or written down once.
    /// </summary>
    private static class TemporaryPassword
    {
        private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
        private const string Lower = "abcdefghijkmnpqrstuvwxyz";
        private const string Digits = "23456789";

        public static string Generate(int length = 16)
        {
            var all = Upper + Lower + Digits;
            var chars = new char[length];
            chars[0] = Pick(Upper);
            chars[1] = Pick(Lower);
            chars[2] = Pick(Digits);
            for (var i = 3; i < length; i++)
                chars[i] = Pick(all);

            RandomNumberGenerator.Shuffle(chars.AsSpan());
            return new string(chars);
        }

        private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
    }
}