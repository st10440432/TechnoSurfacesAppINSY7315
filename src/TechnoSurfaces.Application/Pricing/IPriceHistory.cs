namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// The one way a material price or a rate is changed. A new figure never overwrites
/// the old one: the price in force is closed on the day before the new one starts,
/// so quotes created earlier keep resolving to the figure they were priced at.
///
/// Only the Managing Director may change prices (US-23). The caller enforces that
/// with the CanEditCatalogue policy; this interface does not know who is signed in.
/// </summary>
public interface IPriceHistory
{
    /// <summary>
    /// Sets the price per square metre for a colour or a band at a sheet size from
    /// a date. Pass exactly one of <paramref name="colourId"/> and
    /// <paramref name="priceBandId"/>, matching how the supplier prices.
    /// </summary>
    Task<int> SetMaterialPriceAsync(int? colourId, int? priceBandId, int sheetSizeId,
        decimal pricePerSqm, DateOnly from, string capturedByUserId, CancellationToken ct = default);

    /// <summary>
    /// Sets a rate from a date, for all suppliers when <paramref name="supplierId"/>
    /// is null. Also gives a first price to a rate that has none, such as the lines
    /// awaiting the client's figures.
    /// </summary>
    Task<int> SetRateAsync(int rateItemId, int? supplierId, decimal amount, DateOnly from, CancellationToken ct = default);
}
