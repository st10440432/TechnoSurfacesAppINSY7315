using Microsoft.AspNetCore.Identity;
using TechnoSurfaces.Infrastructure.Data;
using DomainRole = TechnoSurfaces.Domain.UserRole;
using DomainUser = TechnoSurfaces.Domain.People.AppUser;

namespace TechnoSurfacesApp.Identity;

/// <summary>
/// Creates the two roles in every environment, and - in Development only - the
/// prototype's users so the app can be signed in to locally.
///
/// Each development user exists twice by design: an Identity account (credentials)
/// and a domain AppUser (role, active flag). The two share the same Id.
/// The development password is read from user secrets, never from source control.
/// </summary>
public static class IdentitySeeder
{
    private sealed record DevelopmentAccount(string Email, string FullName, string Role, bool IsActive);

    private static readonly DevelopmentAccount[] DevelopmentAccounts =
    {
        new("paul@technosurfaces.co.za",    "Paul Schluter",  Roles.ManagingDirector, IsActive: true),
        new("lerato@technosurfaces.co.za",  "Lerato Mokoena", Roles.Estimator,        IsActive: true),
        new("devan@technosurfaces.co.za",   "Devan Naidoo",   Roles.Estimator,        IsActive: true),

        // Deactivated, so US-26 can be shown: correct password, sign-in still refused.
        new("renaldo@technosurfaces.co.za", "Renaldo Fisher", Roles.Estimator,        IsActive: false),
    };

    public static async Task SeedAsync(
        IServiceProvider services, IConfiguration configuration, ILogger logger, bool includeDevelopmentAccounts)
    {
        using var scope = services.CreateScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var domain = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        foreach (var role in Roles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
                await roleManager.CreateAsync(new IdentityRole(role));
        }

        if (!includeDevelopmentAccounts)
        {
            await SeedInitialManagingDirectorAsync(userManager, domain, configuration, logger);
            return;
        }


        var password = configuration["Seed:DevelopmentPassword"];
        if (string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning("Seed:DevelopmentPassword is not set in user secrets; development accounts were not created.");
            return;
        }

        foreach (var dev in DevelopmentAccounts)
        {
            var account = await userManager.FindByEmailAsync(dev.Email);
            if (account is null)
            {
                account = new UserAccount
                {
                    UserName = dev.Email,
                    Email = dev.Email,
                    EmailConfirmed = true,
                    MustChangePassword = false
                };

                var created = await userManager.CreateAsync(account, password);
                if (!created.Succeeded)
                    throw new InvalidOperationException(
                        $"Could not create development account {dev.Email}: " +
                        string.Join("; ", created.Errors.Select(e => e.Description)));

                await userManager.AddToRoleAsync(account, dev.Role);
            }

            // The domain user shares the Identity account's Id.
            if (await domain.Users.FindAsync(account.Id) is null)
            {
                domain.Users.Add(new DomainUser
                {
                    Id = account.Id,
                    UserName = dev.Email,
                    FullName = dev.FullName,
                    Email = dev.Email,
                    Role = dev.Role == Roles.ManagingDirector ? DomainRole.ManagingDirector : DomainRole.Estimator,
                    IsActive = dev.IsActive,
                    CreatedAtUtc = DateTime.UtcNow
                });
            }
        }

        await domain.SaveChangesAsync();
    }

    /// <summary>
    /// Production has no development accounts, so nobody could sign in to create the
    /// first one. On first start, if no Managing Director exists and the three
    /// Seed:InitialAdmin* settings are present (Key Vault secrets
    /// Seed--InitialAdminEmail, Seed--InitialAdminName, Seed--InitialAdminPassword),
    /// the MD's account is created and must change its password at first sign-in -
    /// so the Key Vault password is used exactly once. Once an MD exists this does
    /// nothing, and the secrets can be removed.
    /// </summary>
    private static async Task SeedInitialManagingDirectorAsync(
        UserManager<UserAccount> userManager, TechnoSurfacesDbContext domain,
        IConfiguration configuration, ILogger logger)
    {
        if ((await userManager.GetUsersInRoleAsync(Roles.ManagingDirector)).Count > 0)
            return;

        var email = configuration["Seed:InitialAdminEmail"];
        var name = configuration["Seed:InitialAdminName"];
        var password = configuration["Seed:InitialAdminPassword"];

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(password))
        {
            logger.LogWarning(
                "No Managing Director account exists and Seed:InitialAdmin* is not configured; nobody can sign in until it is.");
            return;
        }

        var account = new UserAccount
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            MustChangePassword = true
        };

        var created = await userManager.CreateAsync(account, password);
        if (!created.Succeeded)
            throw new InvalidOperationException(
                "Could not create the initial Managing Director account: " +
                string.Join("; ", created.Errors.Select(e => e.Description)));

        await userManager.AddToRoleAsync(account, Roles.ManagingDirector);

        domain.Users.Add(new DomainUser
        {
            Id = account.Id,
            UserName = email,
            FullName = name,
            Email = email,
            Role = DomainRole.ManagingDirector,
            IsActive = true,
            CreatedAtUtc = DateTime.UtcNow
        });
        await domain.SaveChangesAsync();

        // No email or name in the log (POPIA).
        logger.LogInformation("Initial Managing Director account created; its password must be changed at first sign-in.");
    }
}