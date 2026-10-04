using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Platform;

/// Brings both databases up to date and loads the catalogue, the rate card and
/// the standing quotation terms.
public static class DatabaseStartup
{
    public static async Task InitialiseAsync(WebApplication app)
    {
        var migrate = app.Environment.IsDevelopment()
            || app.Configuration.GetValue<bool>("Database:MigrateOnStartup");

        if (!migrate)
            return;

        using var scope = app.Services.CreateScope();
        var authDb = scope.ServiceProvider.GetRequiredService<AuthDbContext>();
        var domainDb = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        await authDb.Database.MigrateAsync();
        await domainDb.Database.MigrateAsync();
        await CatalogueSeeder.SeedAsync(domainDb);
        await RateCardSeeder.SeedAsync(domainDb);
        await QuotationTermsSeeder.SeedAsync(domainDb);
    }
}
