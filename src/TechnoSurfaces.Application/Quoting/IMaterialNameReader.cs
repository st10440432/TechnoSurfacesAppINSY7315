namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// What a customer may read about the material behind a price: the brand, the
/// product line, the thickness and the colour (client request). Nothing else about
/// the material reaches the customer quotation (US-11).
/// </summary>
/// <param name="Brand">Null when the product line has no brand.</param>
/// <param name="Colour">The colour of a price set for one colour; null for a band price.</param>
/// <param name="BandColours">Every colour in the band, for a band price; empty otherwise.</param>
public sealed record MaterialPriceName(
    string? Brand,
    string ProductLine,
    int ThicknessMm,
    string? Colour,
    IReadOnlyList<string> BandColours);

/// <summary>
/// Reads <see cref="MaterialPriceName"/> for material prices. Declared here so the
/// quotation can be written without depending on persistence; implemented in the
/// Infrastructure layer.
/// </summary>
public interface IMaterialNameReader
{
    /// <summary>The names behind the given material prices, by price id, in one query.</summary>
    Task<IReadOnlyDictionary<int, MaterialPriceName>> ForMaterialPricesAsync(
        IReadOnlyCollection<int> materialPriceIds, CancellationToken ct = default);
}
