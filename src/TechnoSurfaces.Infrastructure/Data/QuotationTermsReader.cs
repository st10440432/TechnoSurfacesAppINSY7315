using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

public sealed class QuotationTermsReader : IQuotationTermsReader
{
    private readonly TechnoSurfacesDbContext _db;

    public QuotationTermsReader(TechnoSurfacesDbContext db) => _db = db;

    public async Task<IReadOnlyList<StandingTerm>> GetStandingTermsAsync(CancellationToken ct = default) =>
        await _db.QuotationTerms.AsNoTracking()
            .Where(t => t.IsActive)
            .OrderBy(t => t.Section).ThenBy(t => t.SortOrder)
            .Select(t => new StandingTerm(t.Section, t.Text))
            .ToListAsync(ct);

    public async Task<BrandWarranty?> GetWarrantyForProductLineAsync(int productLineId, CancellationToken ct = default)
    {
        var brand = await _db.ProductLines.AsNoTracking()
            .Where(p => p.Id == productLineId)
            .Select(p => p.Brand)
            .FirstOrDefaultAsync(ct);

        if (brand is null || brand.MaterialWarranty is null || brand.WorkmanshipWarranty is null)
            return null;

        return new BrandWarranty(brand.Name, brand.MaterialWarranty, brand.WorkmanshipWarranty);
    }

    public async Task<IReadOnlyList<BrandWarranty>> GetWarrantiesForMaterialPricesAsync(
        IReadOnlyCollection<int> materialPriceIds, CancellationToken ct = default)
    {
        if (materialPriceIds.Count == 0)
            return Array.Empty<BrandWarranty>();

        // A price hangs off a colour (item-priced suppliers) or a band (band-priced
        // suppliers); both belong to a product line, which carries the brand.
        var productLineIds = await _db.MaterialPrices.AsNoTracking()
            .Where(p => materialPriceIds.Contains(p.Id))
            .Select(p => p.ColourId != null ? p.Colour!.ProductLineId : p.PriceBand!.ProductLineId)
            .Distinct()
            .ToListAsync(ct);

        var brands = await _db.ProductLines.AsNoTracking()
            .Where(l => productLineIds.Contains(l.Id) && l.Brand != null
                     && l.Brand.MaterialWarranty != null && l.Brand.WorkmanshipWarranty != null)
            .Select(l => l.Brand!)
            .ToListAsync(ct);

        return brands
            .DistinctBy(b => b.Id)
            .OrderBy(b => b.Name)
            .Select(b => new BrandWarranty(b.Name, b.MaterialWarranty!, b.WorkmanshipWarranty!))
            .ToList();
    }
}
