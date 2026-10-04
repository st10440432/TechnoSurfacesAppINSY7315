using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data.Seed;

/// <summary>
/// Seeds the standing content of the customer quotation (US-12) and the brand
/// warranties (US-13), taken from the client's quotation template as recorded in
/// Task1/Quotation_Template_Analysis.md.
///
/// The template states warranties for three brands only: DuPont Corian, and
/// Avonite and Staron. Those three are seeded with their periods. The Staron
/// product lines are linked to Staron. No other product line is
/// given a brand, because nothing the client supplied says which warranty the
/// Surface Studio, Max on Top, Woodcentre or Perago and Magicstone materials carry.
/// Their quotations print no warranty until the Managing Director sets one.
///
/// The template's bank details are not seeded. The repository is public and the
/// client's banking details are covered by the non-disclosure agreement. The
/// Managing Director enters them as the BankDetails section of the terms; until
/// then the quotation shows that they are not set.
///
/// Must run after <see cref="CatalogueSeeder"/>, so the Staron product lines exist
/// to be linked.
/// </summary>
public static class QuotationTermsSeeder
{
    public const string StaronBrand = "Staron";

    public static async Task SeedAsync(TechnoSurfacesDbContext db, CancellationToken ct = default)
    {
        await SeedTermsAsync(db, ct);
        await SeedBrandsAsync(db, ct);
    }

    private static async Task SeedTermsAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        if (await db.QuotationTerms.AnyAsync(ct)) return;

        void Add(TermSection section, params string[] lines)
        {
            var order = 0;
            foreach (var line in lines)
                db.QuotationTerms.Add(new QuotationTerm { Section = section, Text = line, SortOrder = ++order });
        }

        Add(TermSection.Notes,
            "Any span in carcass greater than 700mm must be re-enforced");

        Add(TermSection.LeadTimes,
            "Material: 2 working days if stock available at suppliers; 6 to 8 weeks if not in stock and needs to be imported",
            "Production: 5 working days from acceptance of quotation in writing and payment of deposit if applicable");

        Add(TermSection.Exclusions,
            "Sealing between countertop and walls",
            "Any plumbing or electrical work",
            "Removal of existing worktops");

        Add(TermSection.TermsAndConditions,
            "Quotation valid for 30 days only",
            "Damage caused by other parties/trades is charged as extra",
            "No liability for negligent damage once handed over, or once signed \"Received in Good Order\"",
            "No work commences until written acceptance and deposit paid, or an official order number for account customers",
            "Prices exclude VAT and delivery unless stated",
            "All pricing subject to final measurement on site; changes are charged for",
            "No contra charges, set off or retentions unless agreed in writing beforehand");

        Add(TermSection.Disclaimers,
            "No warranty where material is used in an application the manufacturer does not support",
            "Solid dark colours scratch more easily than lighter speckled colours — not recommended for kitchen worktops or high-traffic areas",
            "Veined or glitter colours may not give a seamless finish due to the directional nature of the material",
            "No warranty where material is in direct sunlight or variable weather");

        Add(TermSection.Warranties,
            "Techno Surfaces is a DuPont certified installer.");

        Add(TermSection.PaymentTerms,
            "60% deposit on order, balance on completion.");

        await db.SaveChangesAsync(ct);
    }

    private static async Task SeedBrandsAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        if (await db.Brands.AnyAsync(ct)) return;

        db.Brands.Add(new Brand { Name = "DuPont Corian", MaterialWarranty = "10 years", WorkmanshipWarranty = "10 years (limited)" });
        db.Brands.Add(new Brand { Name = "Avonite", MaterialWarranty = "10 years", WorkmanshipWarranty = "1 year" });
        var staron = new Brand { Name = StaronBrand, MaterialWarranty = "10 years", WorkmanshipWarranty = "1 year" };
        db.Brands.Add(staron);
        await db.SaveChangesAsync(ct);

        var staronLines = await db.ProductLines
            .Where(p => p.Name == "Staron" && p.Supplier!.Name == "Staron (Salvocorp)" && p.BrandId == null)
            .ToListAsync(ct);

        foreach (var line in staronLines)
            line.BrandId = staron.Id;

        await db.SaveChangesAsync(ct);
    }
}
