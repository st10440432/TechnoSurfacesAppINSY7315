using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// EF Core implementation of the catalogue queries the pricing strategies need.
///
/// Every lookup here is on the read path of a price resolution, which NFR-05
/// requires to complete within two seconds, so the queries are keyed on the indexes
/// declared in the model configuration and take only the single row in force.
/// </summary>
public sealed class CatalogueReader : ICatalogueReader
{
    private readonly TechnoSurfacesDbContext _db;

    public CatalogueReader(TechnoSurfacesDbContext db) => _db = db;

    public Task<Colour?> GetColourAsync(int colourId, CancellationToken ct = default) =>
        _db.Colours
            .AsNoTracking()
            .Include(c => c.PriceBand)
            .Include(c => c.ProductLine)!.ThenInclude(p => p!.Supplier)
            .FirstOrDefaultAsync(c => c.Id == colourId, ct);

    public Task<SheetSize?> GetSheetSizeAsync(int sheetSizeId, CancellationToken ct = default) =>
        _db.SheetSizes
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sheetSizeId, ct);

    public Task<Supplier?> GetSupplierForColourAsync(int colourId, CancellationToken ct = default) =>
        _db.Colours
            .AsNoTracking()
            .Where(c => c.Id == colourId)
            .Select(c => c.ProductLine!.Supplier)
            .FirstOrDefaultAsync(ct);

    public Task<MaterialPrice?> FindPriceByColourAsync(int colourId, int sheetSizeId, DateOnly asAt, CancellationToken ct = default) =>
        _db.MaterialPrices
            .AsNoTracking()
            .Where(p => p.ColourId == colourId
                     && p.SheetSizeId == sheetSizeId
                     && p.EffectiveFrom <= asAt
                     && (p.EffectiveTo == null || p.EffectiveTo >= asAt))
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

    public Task<MaterialPrice?> FindPriceByBandAsync(int priceBandId, int sheetSizeId, DateOnly asAt, CancellationToken ct = default) =>
        _db.MaterialPrices
            .AsNoTracking()
            .Where(p => p.PriceBandId == priceBandId
                     && p.SheetSizeId == sheetSizeId
                     && p.EffectiveFrom <= asAt
                     && (p.EffectiveTo == null || p.EffectiveTo >= asAt))
            .OrderByDescending(p => p.EffectiveFrom)
            .FirstOrDefaultAsync(ct);

    public Task<RateItem?> GetRateItemAsync(int rateItemId, CancellationToken ct = default) =>
        _db.RateItems
            .AsNoTracking()
            .Include(r => r.DerivedFromRateItem)
            .FirstOrDefaultAsync(r => r.Id == rateItemId, ct);

    public async Task<IReadOnlyList<RateItem>> GetRateCardAsync(DateOnly asAt, CancellationToken ct = default) =>
        await _db.RateItems
            .AsNoTracking()
            .Include(r => r.Prices.Where(p => p.SupplierId == null
                                           && p.EffectiveFrom <= asAt
                                           && (p.EffectiveTo == null || p.EffectiveTo >= asAt)))
            .ToListAsync(ct);

    public Task<RatePrice?> FindRatePriceAsync(int rateItemId, int? supplierId, DateOnly asAt, CancellationToken ct = default) =>
        _db.RatePrices
            .AsNoTracking()
            .Where(r => r.RateItemId == rateItemId
                     && (r.SupplierId == supplierId || r.SupplierId == null)
                     && r.EffectiveFrom <= asAt
                     && (r.EffectiveTo == null || r.EffectiveTo >= asAt))
            // A supplier-specific rate wins over the general one.
            .OrderByDescending(r => r.SupplierId.HasValue)
            .ThenByDescending(r => r.EffectiveFrom)
            .FirstOrDefaultAsync(ct);
}
