namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// The price of a sheet, for a given colour or price band, in a given sheet size,
/// over a stated period of validity.
///
/// Separated from <see cref="Colour"/> so that price history is preserved, which is
/// required because a quote must retain the price at which it was created. Prices
/// are versioned rather than overwritten.
/// </summary>
public class MaterialPrice
{
    public int Id { get; set; }

    /// <summary>
    /// Set for suppliers who price each colour individually. Exactly one of
    /// <see cref="ColourId"/> and <see cref="PriceBandId"/> is populated; enforced
    /// by a check constraint.
    /// </summary>
    public int? ColourId { get; set; }
    public Colour? Colour { get; set; }

    /// <summary>Set for suppliers who price by band.</summary>
    public int? PriceBandId { get; set; }
    public PriceBand? PriceBand { get; set; }

    /// <summary>Price is always specific to a sheet size.</summary>
    public int SheetSizeId { get; set; }
    public SheetSize? SheetSize { get; set; }

    /// <summary>
    /// Price per square metre. Price per sheet is derived from this and the sheet
    /// area rather than stored independently, because storing both invites them to
    /// drift apart, which is the exact failure this system exists to remove.
    ///
    /// Held to four decimal places. Band-priced suppliers publish this figure
    /// directly and it is a round number; item-priced suppliers publish only a
    /// sheet price, so this is derived from it and needs the extra places to round
    /// back to the published cent.
    /// </summary>
    public decimal PricePerSqm { get; set; }

    /// <summary>
    /// Derives the per-square-metre figure from a published sheet price. Used when
    /// capturing prices from a supplier who prices each item individually.
    /// </summary>
    public static decimal PerSqmFromSheetPrice(decimal sheetPrice, SheetSize size) =>
        decimal.Round(sheetPrice / size.AreaM2, 4, MidpointRounding.AwayFromZero);

    public DateOnly EffectiveFrom { get; set; }

    /// <summary>Null means currently in force.</summary>
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>The user who captured the price. Only the Managing Director may.</summary>
    public string? CapturedByUserId { get; set; }

    public DateTime CapturedAtUtc { get; set; }

    public bool IsInForceOn(DateOnly date) =>
        EffectiveFrom <= date && (EffectiveTo is null || EffectiveTo >= date);

    /// <summary>
    /// Replaces this price from a date. This price closes on the day before, so the
    /// two are never in force on the same day and a quote dated before the change
    /// still resolves to the price it was created under. Prices are versioned, never
    /// overwritten.
    /// </summary>
    public MaterialPrice Supersede(decimal pricePerSqm, DateOnly from, string capturedByUserId)
    {
        PricePeriod.EnsureCanSupersede(EffectiveFrom, EffectiveTo, from);
        if (pricePerSqm <= 0)
            throw new ArgumentOutOfRangeException(nameof(pricePerSqm), "A material price must be greater than zero.");

        EffectiveTo = from.AddDays(-1);

        return new MaterialPrice
        {
            ColourId = ColourId,
            PriceBandId = PriceBandId,
            SheetSizeId = SheetSizeId,
            PricePerSqm = pricePerSqm,
            EffectiveFrom = from,
            CapturedByUserId = capturedByUserId,
            CapturedAtUtc = DateTime.UtcNow
        };
    }

    /// <summary>
    /// price per sheet = price per square metre x (length x width). Verified to the
    /// cent against the Staron, Perago and Surface Studio published figures.
    /// </summary>
    public decimal PricePerSheet(SheetSize size) =>
        decimal.Round(PricePerSqm * size.AreaM2, 2, MidpointRounding.AwayFromZero);
}
