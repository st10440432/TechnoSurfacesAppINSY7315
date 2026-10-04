using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Platform;

/// Brings both databases up to date and loads the catalogue, the rate card, the
/// standing quotation terms and the starting customer.
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
        await BrandWarrantySeeder.SeedAsync(domainDb);

        // RA Woodcraft, the real customer from invoice IN114317, so a quote can be
        // made as soon as the system is live.
        await CustomerSeeder.SeedAsync(domainDb);
    }
}
