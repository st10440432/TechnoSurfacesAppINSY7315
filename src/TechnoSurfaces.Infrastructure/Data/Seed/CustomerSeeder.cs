using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Infrastructure.Data.Seed;

/// <summary>
/// Seeds the customer a fresh database starts with, in every environment, so a
/// quote can be made as soon as the system is live.
///
/// RA Woodcraft is a real customer, taken from the client's Pastel invoice IN114317:
/// the name, account code RAW001, billing address and tax reference are as printed
/// there. The invoice names no person, so its contact is the role "Accounts" rather
/// than an invented name. Invented people are not seeded anywhere.
///
/// The customer is added only if its account code is not already present, so the
/// seeder can run on every start-up.
/// </summary>
public static class CustomerSeeder
{
    public const string AccountCode = "RAW001";

    public static async Task SeedAsync(TechnoSurfacesDbContext db, CancellationToken ct = default)
    {
        if (await db.Customers.AnyAsync(c => c.AccountCode == AccountCode, ct))
            return;

        db.Customers.Add(new Customer
        {
            Name = "RA Woodcraft",
            AccountCode = AccountCode,
            AddressLine1 = "Unit 5 Haryn Park",
            AddressLine2 = "13 Mocke Road",
            City = "Diep River",
            VatNumber = "4690293412",
            Contacts = { new Contact { FullName = "Accounts" } }
        });
        await db.SaveChangesAsync(ct);
    }
}
