using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// The catalogue queries the pricing strategies need. Implemented in the
/// Infrastructure layer over EF Core; declared here so that the Application layer
/// does not depend on persistence.
/// </summary>
public interface ICatalogueReader
{
    /// <summary>Loads a colour with its product line, supplier and price band.</summary>
    Task<Colour?> GetColourAsync(int colourId, CancellationToken ct = default);

    Task<SheetSize?> GetSheetSizeAsync(int sheetSizeId, CancellationToken ct = default);

    /// <summary>The supplier behind a colour, which selects the pricing strategy.</summary>
    Task<Supplier?> GetSupplierForColourAsync(int colourId, CancellationToken ct = default);

    /// <summary>
    /// The price row for a colour priced individually, in force on the given date.
    /// Supported by the index on (ColourId, SheetSizeId, EffectiveFrom).
    /// </summary>
    Task<MaterialPrice?> FindPriceByColourAsync(int colourId, int sheetSizeId, DateOnly asAt, CancellationToken ct = default);

    /// <summary>The price row for a band-priced colour, in force on the given date.</summary>
    Task<MaterialPrice?> FindPriceByBandAsync(int priceBandId, int sheetSizeId, DateOnly asAt, CancellationToken ct = default);

    /// <summary>The rate amount in force on the given date, optionally for a supplier.</summary>
    Task<RatePrice?> FindRatePriceAsync(int rateItemId, int? supplierId, DateOnly asAt, CancellationToken ct = default);

    /// <summary>A rate item, including the item its rate is derived from where one is set.</summary>
    Task<RateItem?> GetRateItemAsync(int rateItemId, CancellationToken ct = default);

    /// <summary>
    /// Every rate item, retired ones included because a live rate can derive from
    /// them, each carrying only its general prices (no supplier) in force on the
    /// date. One query, for screens that price the whole rate card at once.
    /// </summary>
    Task<IReadOnlyList<RateItem>> GetRateCardAsync(DateOnly asAt, CancellationToken ct = default);
}
