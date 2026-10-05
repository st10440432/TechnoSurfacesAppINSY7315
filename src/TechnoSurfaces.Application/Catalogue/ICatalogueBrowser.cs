namespace TechnoSurfaces.Application.Catalogue;

/// <summary>One step of the cascading material choice: the suppliers.</summary>
public sealed record SupplierOption(int Id, string Name, string? TradingAs, string PricingStructure, DateOnly PriceListDated);

/// <summary>A product line of the chosen supplier.</summary>
public sealed record ProductLineOption(int Id, string Name, int ThicknessMm, string Status);

/// <summary>A colour of the chosen product line, with its band where the supplier prices by band.</summary>
public sealed record ColourOption(int Id, string Name, string SupplierCode, string? Range, string? PriceBand, string Status);

/// <summary>A sheet size the chosen colour is supplied in (US-02).</summary>
public sealed record SheetSizeOption(int Id, int LengthMm, int WidthMm, decimal AreaM2);

/// <summary>A line on the rate card that can be added to a quote.</summary>
public sealed record RateItemOption(int Id, string Name, string Category, string Unit, string Derivation, bool IsBelowTheLine);

/// <summary>
/// Read-only lists for the costing sheet's cascading choice (US-01):
/// supplier, then product line, then colour, then sheet size.
///
/// Every list holds only what can be chosen on a new line. A discontinued product
/// line, colour or rate item is left out (US-24) but still resolves on quotes that
/// already carry it. Each method returns null when the parent does not exist, so the
/// caller can tell "no such supplier" from "a supplier with nothing to choose".
///
/// Queries are projections, not entity loads, because NFR-05 allows two seconds for
/// a lookup.
/// </summary>
public interface ICatalogueBrowser
{
    Task<IReadOnlyList<SupplierOption>> SuppliersAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ProductLineOption>?> ProductLinesAsync(int supplierId, CancellationToken ct = default);

    Task<IReadOnlyList<ColourOption>?> ColoursAsync(int productLineId, CancellationToken ct = default);

    Task<IReadOnlyList<SheetSizeOption>?> SheetSizesAsync(int colourId, CancellationToken ct = default);

    Task<IReadOnlyList<RateItemOption>> RateItemsAsync(CancellationToken ct = default);
}
