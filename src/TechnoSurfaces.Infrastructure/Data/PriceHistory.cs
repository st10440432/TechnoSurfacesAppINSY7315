using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// Applies a price change as two saves in one transaction: the current price is
/// closed first, then the new one is added. The database rejects two prices for the
/// same key in force on the same day, so the order matters and is not left to how
/// EF Core happens to batch the statements.
/// </summary>
public sealed class PriceHistory : IPriceHistory
{
    private readonly TechnoSurfacesDbContext _db;

    public PriceHistory(TechnoSurfacesDbContext db) => _db = db;

    public async Task<int> SetMaterialPriceAsync(int? colourId, int? priceBandId, int sheetSizeId,
        decimal pricePerSqm, DateOnly from, string capturedByUserId, CancellationToken ct = default)
    {
        if ((colourId is null) == (priceBandId is null))
            throw new ArgumentException("A material price belongs to a colour or to a band, not both and not neither.");

        var current = await _db.MaterialPrices
            .Where(p => p.SheetSizeId == sheetSizeId
                     && p.EffectiveTo == null
                     && (colourId != null ? p.ColourId == colourId : p.PriceBandId == priceBandId))
            .SingleOrDefaultAsync(ct);

        var next = current?.Supersede(pricePerSqm, from, capturedByUserId)
            ?? FirstMaterialPrice(colourId, priceBandId, sheetSizeId, pricePerSqm, from, capturedByUserId);

        await SaveInOrderAsync(current is not null, () => _db.MaterialPrices.Add(next), ct);
        return next.Id;
    }

    public async Task<int> SetRateAsync(int rateItemId, int? supplierId, decimal amount, DateOnly from, CancellationToken ct = default)
    {
        var item = await _db.RateItems.AsNoTracking().FirstOrDefaultAsync(r => r.Id == rateItemId, ct)
            ?? throw new InvalidOperationException($"Rate item {rateItemId} is not on the rate card.");

        if (item.DerivedFromRateItemId is not null)
            throw new InvalidOperationException(
                $"{item.Name} is calculated from another rate. Change that rate instead.");

        var current = await _db.RatePrices
            .Where(p => p.RateItemId == rateItemId && p.SupplierId == supplierId && p.EffectiveTo == null)
            .SingleOrDefaultAsync(ct);

        var next = current?.Supersede(amount, from)
            ?? FirstRate(rateItemId, supplierId, amount, from);

        await SaveInOrderAsync(current is not null, () => _db.RatePrices.Add(next), ct);
        return next.Id;
    }

    private async Task SaveInOrderAsync(bool closesCurrent, Action addNext, CancellationToken ct)
    {
        var ownsTransaction = _db.Database.CurrentTransaction is null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(ct) : null;

        if (closesCurrent)
            await _db.SaveChangesAsync(ct);

        addNext();
        await _db.SaveChangesAsync(ct);

        if (transaction is not null)
            await transaction.CommitAsync(ct);
    }

    private static MaterialPrice FirstMaterialPrice(int? colourId, int? priceBandId, int sheetSizeId,
        decimal pricePerSqm, DateOnly from, string capturedByUserId)
    {
        if (pricePerSqm <= 0)
            throw new ArgumentOutOfRangeException(nameof(pricePerSqm), "A material price must be greater than zero.");

        return new MaterialPrice
        {
            ColourId = colourId,
            PriceBandId = priceBandId,
            SheetSizeId = sheetSizeId,
            PricePerSqm = pricePerSqm,
            EffectiveFrom = from,
            CapturedByUserId = capturedByUserId,
            CapturedAtUtc = DateTime.UtcNow
        };
    }

    private static RatePrice FirstRate(int rateItemId, int? supplierId, decimal amount, DateOnly from)
    {
        if (amount <= 0)
            throw new ArgumentOutOfRangeException(nameof(amount), "A rate must be greater than zero.");

        return new RatePrice { RateItemId = rateItemId, SupplierId = supplierId, Amount = amount, EffectiveFrom = from };
    }
}
