using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Domain;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// EF Core implementation of <see cref="ICatalogueBrowser"/>. Every query is a
/// no-tracking projection to the option record, so only the columns the cascade
/// shows are read.
/// </summary>
public sealed class CatalogueBrowser : ICatalogueBrowser
{
    private readonly TechnoSurfacesDbContext _db;

    public CatalogueBrowser(TechnoSurfacesDbContext db) => _db = db;

    public async Task<IReadOnlyList<SupplierOption>> SuppliersAsync(CancellationToken ct = default)
    {
        var rows = await _db.Suppliers
            .AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new { s.Id, s.Name, s.TradingAs, s.PricingStructure, s.PriceListDated })
            .ToListAsync(ct);

        return rows
            .Select(s => new SupplierOption(s.Id, s.Name, s.TradingAs, s.PricingStructure.ToString(), s.PriceListDated))
            .ToList();
    }

    public async Task<IReadOnlyList<ProductLineOption>?> ProductLinesAsync(int supplierId, CancellationToken ct = default)
    {
        if (!await _db.Suppliers.AnyAsync(s => s.Id == supplierId, ct))
            return null;

        var rows = await _db.ProductLines
            .AsNoTracking()
            .Where(p => p.SupplierId == supplierId && p.Status != CatalogueStatus.Discontinued)
            .OrderBy(p => p.Name).ThenBy(p => p.ThicknessMm)
            .Select(p => new { p.Id, p.Name, p.ThicknessMm, p.Status })
            .ToListAsync(ct);

        return rows
            .Select(p => new ProductLineOption(p.Id, p.Name, p.ThicknessMm, p.Status.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<ColourOption>?> ColoursAsync(int productLineId, CancellationToken ct = default)
    {
        if (!await _db.ProductLines.AnyAsync(p => p.Id == productLineId, ct))
            return null;

        var rows = await _db.Colours
            .AsNoTracking()
            .Where(c => c.ProductLineId == productLineId && c.Status != CatalogueStatus.Discontinued)
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.SupplierCode,
                c.Range,
                Band = c.PriceBand == null ? null : c.PriceBand.Code,
                c.Status
            })
            .ToListAsync(ct);

        return rows
            .Select(c => new ColourOption(c.Id, c.Name, c.SupplierCode, c.Range, c.Band, c.Status.ToString()))
            .ToList();
    }

    public async Task<IReadOnlyList<SheetSizeOption>?> SheetSizesAsync(int colourId, CancellationToken ct = default)
    {
        var productLineId = await _db.Colours
            .AsNoTracking()
            .Where(c => c.Id == colourId)
            .Select(c => (int?)c.ProductLineId)
            .FirstOrDefaultAsync(ct);

        if (productLineId is null)
            return null;

        var rows = await _db.SheetSizes
            .AsNoTracking()
            .Where(s => s.ProductLineId == productLineId)
            .OrderBy(s => s.LengthMm).ThenBy(s => s.WidthMm)
            .Select(s => new { s.Id, s.LengthMm, s.WidthMm })
            .ToListAsync(ct);

        // Area is derived from the dimensions on the domain type, so it is worked out
        // the same way here rather than stored.
        return rows
            .Select(s => new SheetSizeOption(s.Id, s.LengthMm, s.WidthMm,
                decimal.Round(s.LengthMm / 1000m * (s.WidthMm / 1000m), 4)))
            .ToList();
    }

    public async Task<IReadOnlyList<RateItemOption>> RateItemsAsync(CancellationToken ct = default)
    {
        var rows = await _db.RateItems
            .AsNoTracking()
            .Where(r => r.Status != CatalogueStatus.Discontinued)
            .OrderBy(r => r.SortOrder)
            .Select(r => new { r.Id, r.Name, r.Category, r.Unit, r.Derivation, r.IsBelowTheLine })
            .ToListAsync(ct);

        return rows
            .Select(r => new RateItemOption(r.Id, r.Name, r.Category.ToString(), r.Unit.ToString(),
                r.Derivation.ToString(), r.IsBelowTheLine))
            .ToList();
    }
}
