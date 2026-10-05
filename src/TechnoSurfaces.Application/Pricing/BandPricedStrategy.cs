using TechnoSurfaces.Domain;

namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// Resolves a price for suppliers who publish price bands: Staron, whose Colour
/// Category column groups many colour names into eleven categories, and Surface
/// Studio, whose groups run A1 to A4 and M1 to M4.
///
/// The colour carries no price of its own under this scheme. It points at a band,
/// and the band's price row for the chosen sheet size holds the figure.
/// </summary>
public sealed class BandPricedStrategy : IPriceResolutionStrategy
{
    private readonly ICatalogueReader _catalogue;

    public BandPricedStrategy(ICatalogueReader catalogue) => _catalogue = catalogue;

    public PricingStructure Handles => PricingStructure.Band;

    public async Task<PriceResolution> ResolveAsync(PriceKey key, DateOnly asAt, CancellationToken ct = default)
    {
        var colour = await _catalogue.GetColourAsync(key.ColourId, ct);
        if (colour is null)
            return PriceResolution.Failure($"Colour {key.ColourId} is not in the catalogue.");

        var size = await _catalogue.GetSheetSizeAsync(key.SheetSizeId, ct);
        if (size is null)
            return PriceResolution.Failure($"Sheet size {key.SheetSizeId} is not in the catalogue.");

        if (colour.PriceBandId is null)
            return PriceResolution.Failure(
                $"{colour.Name} is supplied by a band-priced supplier but has not been assigned to a price band.");

        var price = await _catalogue.FindPriceByBandAsync(colour.PriceBandId.Value, key.SheetSizeId, asAt, ct);
        if (price is null)
            return PriceResolution.Failure(
                $"No price is in force on {asAt:yyyy-MM-dd} for band {colour.PriceBand?.Code ?? colour.PriceBandId.Value.ToString()} at {size}.");

        var origin =
            $"{colour.ProductLine?.Supplier?.Name ?? "Supplier"} price band {colour.PriceBand?.Code ?? "?"}, " +
            $"{size}, effective {price.EffectiveFrom:yyyy-MM-dd}";

        return PriceResolution.Success(price.PricePerSheet(size), origin, price.Id);
    }
}
