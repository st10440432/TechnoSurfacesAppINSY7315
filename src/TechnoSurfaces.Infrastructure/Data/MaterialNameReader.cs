using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

public sealed class MaterialNameReader : IMaterialNameReader
{
    private readonly TechnoSurfacesDbContext _db;

    public MaterialNameReader(TechnoSurfacesDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<int, MaterialPriceName>> ForMaterialPricesAsync(
        IReadOnlyCollection<int> materialPriceIds, CancellationToken ct = default)
    {
        if (materialPriceIds.Count == 0)
            return new Dictionary<int, MaterialPriceName>();

        // A price hangs off a colour (item-priced suppliers) or a band (band-priced
        // suppliers); both belong to a product line, which carries the brand. A band
        // price names no colour, so the band's colours come back with it.
        var rows = await _db.MaterialPrices.AsNoTracking()
            .Where(p => materialPriceIds.Contains(p.Id))
            .Select(p => new
            {
                p.Id,
                Brand = p.ColourId != null ? p.Colour!.ProductLine!.Brand!.Name : p.PriceBand!.ProductLine!.Brand!.Name,
                ProductLine = p.ColourId != null ? p.Colour!.ProductLine!.Name : p.PriceBand!.ProductLine!.Name,
                ThicknessMm = p.ColourId != null ? p.Colour!.ProductLine!.ThicknessMm : p.PriceBand!.ProductLine!.ThicknessMm,
                Colour = p.ColourId != null ? p.Colour!.Name : null,
                BandColours = p.PriceBand!.Colours.Select(c => c.Name).ToList()
            })
            .ToListAsync(ct);

        return rows.ToDictionary(
            r => r.Id,
            r => new MaterialPriceName(r.Brand, r.ProductLine, r.ThicknessMm, r.Colour, r.BandColours));
    }
}
