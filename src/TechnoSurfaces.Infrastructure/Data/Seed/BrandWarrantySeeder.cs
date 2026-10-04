using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data.Seed;

/// <summary>
/// Gives the brands with no warranty of their own the Staron and Avonite wording:
/// 10 years on the material, 1 year on workmanship. Team decision of 4 October:
/// every brand except DuPont Corian is quoted with that wording (US-13).
///
/// Runs after QuotationTermsSeeder, which creates Corian, Avonite and Staron. Only
/// product lines that have no brand yet are given one, and an existing brand's
/// wording is never changed, so anything the Managing Director has set on the
/// catalogue screens stands. Safe to run on every start-up.
/// </summary>
public static class BrandWarrantySeeder
{
    public const string MaterialWarranty = "10 years";
    public const string WorkmanshipWarranty = "1 year";

    /// <summary>Each brand, and the seeded product lines sold under it.</summary>
    private static readonly (string Brand, string[] ProductLines)[] Brands =
    {
        ("Infinito", new[] { "Infinito Full Acrylic", "Infinito Modified" }),
        ("Max on Top", new[] { "Max Pure Solid Surface", "Max Modified Acrylic", "Max Getacore" }),
        ("Surwell", new[] { "Surwell Modified Acrylic" }),
        ("SchemaR", new[] { "SchemaR Full Acrylic" }),
        ("Perago", new[] { "Perago 100% Acrylic" }),
        ("Magicstone", new[] { "Magicstone Modified" }),
    };

    public static async Task SeedAsync(TechnoSurfacesDbContext db, CancellationToken ct = default)
    {
        foreach (var (brandName, productLineNames) in Brands)
        {
            var unbranded = await db.ProductLines
                .Where(p => productLineNames.Contains(p.Name) && p.BrandId == null)
                .ToListAsync(ct);
            if (unbranded.Count == 0)
                continue;

            var brand = await db.Brands.FirstOrDefaultAsync(b => b.Name == brandName, ct);
            if (brand is null)
            {
                brand = new Brand { Name = brandName, MaterialWarranty = MaterialWarranty, WorkmanshipWarranty = WorkmanshipWarranty };
                db.Brands.Add(brand);
                await db.SaveChangesAsync(ct);
            }

            foreach (var line in unbranded)
                line.BrandId = brand.Id;

            await db.SaveChangesAsync(ct);
        }
    }
}
