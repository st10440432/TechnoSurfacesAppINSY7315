using TechnoSurfaces.Domain;

namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// Resolves a price for suppliers who price each colour individually: Max on Top,
/// Woodcentre and Perago/Magicstone. There are no bands at all on these lists, so
/// the price row hangs directly off the colour.
///
/// The same colour at a different sheet size is a different price. Max on Top list
/// Glacier White 8016 at R3 991 for 3680 x 760 in 12mm and R2 287 for 2440 x 920 in
/// 6mm, which is why the sheet size is part of the key rather than a display
/// attribute.
/// </summary>
public sealed class ItemPricedStrategy : IPriceResolutionStrategy
{
    private readonly ICatalogueReader _catalogue;

    public ItemPricedStrategy(ICatalogueReader catalogue) => _catalogue = catalogue;

    public PricingStructure Handles => PricingStructure.Item;

    public async Task<PriceResolution> ResolveAsync(PriceKey key, DateOnly asAt, CancellationToken ct = default)
    {
        var colour = await _catalogue.GetColourAsync(key.ColourId, ct);
        if (colour is null)
            return PriceResolution.Failure($"Colour {key.ColourId} is not in the catalogue.");

        var size = await _catalogue.GetSheetSizeAsync(key.SheetSizeId, ct);
        if (size is null)
            return PriceResolution.Failure($"Sheet size {key.SheetSizeId} is not in the catalogue.");

        var price = await _catalogue.FindPriceByColourAsync(key.ColourId, key.SheetSizeId, asAt, ct);
        if (price is null)
            return PriceResolution.Failure(
                $"No price is in force on {asAt:yyyy-MM-dd} for {colour.Name} at {size}.");

        var origin =
            $"{colour.ProductLine?.Supplier?.Name ?? "Supplier"} {colour.Name} " +
            $"({colour.SupplierCode}), {size}, effective {price.EffectiveFrom:yyyy-MM-dd}";

        return PriceResolution.Success(price.PricePerSheet(size), origin, price.Id);
    }
}
