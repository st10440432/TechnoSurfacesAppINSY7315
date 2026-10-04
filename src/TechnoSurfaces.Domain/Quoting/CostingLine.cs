using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// A single line of the internal costing: what was used, in what quantity, at what
/// unit cost, and with any discount received from the supplier. Never shown to the
/// customer.
///
/// The resolved unit price is copied onto the line when it is created. A later
/// catalogue change therefore cannot alter an existing quote, which is US-22 and
/// NFR-11. <see cref="MaterialPriceId"/> is retained only so the price row that was
/// used can be traced; it is not read back when totalling.
/// </summary>
public class CostingLine
{
    private CostingLine() { }

    private CostingLine(
        CostingLineType lineType,
        string description,
        decimal resolvedUnitPrice,
        string priceOrigin,
        decimal quantity,
        decimal supplierDiscountPercent,
        bool isBelowTheLine,
        DerivationRule derivation)
    {
        // NFR-01: no line is ever priced at zero. A price that did not resolve
        // never reaches this point, and a resolved price of zero is refused here
        // for the same reason: it is the plausible wrong figure the spreadsheet
        // produced.
        if (resolvedUnitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(resolvedUnitPrice), "A line cannot be priced at zero or less.");
        GuardQuantity(quantity);
        GuardDiscount(supplierDiscountPercent);
        if (string.IsNullOrWhiteSpace(priceOrigin))
            throw new ArgumentException("Every priced line must record where its price came from.", nameof(priceOrigin));

        LineType = lineType;
        Description = description;
        ResolvedUnitPrice = resolvedUnitPrice;
        PriceOrigin = priceOrigin;
        Quantity = quantity;
        SupplierDiscountPercent = supplierDiscountPercent;
        IsBelowTheLine = isBelowTheLine;
        Derivation = derivation;
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    public CostingLineType LineType { get; private set; }

    /// <summary>Set on a material line. Traceability only; the price is copied below.</summary>
    public int? MaterialPriceId { get; private set; }

    /// <summary>Set on a rate line.</summary>
    public int? RateItemId { get; private set; }

    /// <summary>What the estimator sees. Captured at creation so it survives a catalogue change.</summary>
    public string Description { get; private set; } = "";

    /// <summary>
    /// The price actually used, copied from the catalogue at creation. This is what
    /// the calculator reads.
    /// </summary>
    public decimal ResolvedUnitPrice { get; private set; }

    /// <summary>
    /// Where the price came from, shown on the line. NFR-01 requires the origin to
    /// be visible, for example "Staron price band Supreme, 3680 x 760, effective
    /// 2025-03-01".
    /// </summary>
    public string PriceOrigin { get; private set; } = "";

    /// <summary>
    /// Not an integer. Woodcentre quote stock in half sheets, and area-derived
    /// quantities are fractional.
    /// </summary>
    public decimal Quantity { get; private set; }

    public decimal SupplierDiscountPercent { get; private set; }

    /// <summary>
    /// A rate the estimator typed for this quote only (US-06). The catalogue price
    /// in <see cref="ResolvedUnitPrice"/> is kept beside it, so the change is
    /// visible and can be undone, and the rate card itself is not touched.
    /// </summary>
    public decimal? OverriddenUnitPrice { get; private set; }

    public bool HasPriceOverride => OverriddenUnitPrice is not null;

    /// <summary>
    /// Set when the estimator typed over a derived quantity, for example silicon
    /// on a job that needs more than two per sheet. The calculator then leaves the
    /// quantity as typed.
    /// </summary>
    public bool IsQuantityOverridden { get; private set; }

    /// <summary>
    /// Snapshot of the rate item's below-the-line flag. Items below the line are
    /// cost recovery and are not marked up.
    /// </summary>
    public bool IsBelowTheLine { get; private set; }

    public DerivationRule Derivation { get; private set; }

    /// <summary>
    /// Snapshot of the rate item's derivation factor. Silicon and sealing is sheets
    /// multiplied by two; consumables and transport carry 1.
    /// </summary>
    public decimal DerivationFactor { get; private set; } = 1m;

    /// <summary>Sheet area, on a material line. Drives the area-derived quantities.</summary>
    public decimal? SheetAreaM2 { get; private set; }

    public int SortOrder { get; set; }

    /// <summary>The rate this line is charged at: the override if there is one.</summary>
    public decimal UnitPrice => OverriddenUnitPrice ?? ResolvedUnitPrice;

    /// <summary>
    /// effectiveUnitPrice = unitPrice x (1 - supplierDiscountPercent / 100)
    /// </summary>
    public decimal EffectiveUnitPrice() =>
        decimal.Round(UnitPrice * (1m - SupplierDiscountPercent / 100m), 2, MidpointRounding.AwayFromZero);

    /// <summary>
    /// lineTotal = effectiveUnitPrice x quantity
    /// </summary>
    public decimal LineTotal() =>
        decimal.Round(EffectiveUnitPrice() * Quantity, 2, MidpointRounding.AwayFromZero);

    /// <summary>Total area contributed by this line, used for derived quantities.</summary>
    public decimal AreaM2() =>
        LineType == CostingLineType.Material && SheetAreaM2 is not null
            ? decimal.Round(SheetAreaM2.Value * Quantity, 4)
            : 0m;

    /// <summary>Sheets contributed by this line, used for derived quantities.</summary>
    public decimal SheetCount() =>
        LineType == CostingLineType.Material ? Quantity : 0m;

    public static CostingLine ForMaterial(
        int materialPriceId,
        string description,
        decimal resolvedUnitPrice,
        string priceOrigin,
        decimal quantity,
        decimal sheetAreaM2,
        decimal supplierDiscountPercent = 0m)
    {
        var line = new CostingLine(
            CostingLineType.Material, description, resolvedUnitPrice, priceOrigin,
            quantity, supplierDiscountPercent, isBelowTheLine: false, DerivationRule.Entered)
        {
            MaterialPriceId = materialPriceId,
            SheetAreaM2 = sheetAreaM2
        };
        return line;
    }

    public static CostingLine ForRate(
        int rateItemId,
        string description,
        decimal resolvedUnitPrice,
        string priceOrigin,
        decimal quantity,
        bool isBelowTheLine,
        DerivationRule derivation = DerivationRule.Entered,
        decimal derivationFactor = 1m)
    {
        var line = new CostingLine(
            CostingLineType.Rate, description, resolvedUnitPrice, priceOrigin,
            quantity, supplierDiscountPercent: 0m, isBelowTheLine, derivation)
        {
            RateItemId = rateItemId,
            DerivationFactor = derivationFactor
        };
        return line;
    }

    public bool IsDerived => Derivation != DerivationRule.Entered;

    /// <summary>
    /// A copy of this line for a new version of the quote. Everything is carried
    /// across as it stands, including the copied price and any override; the price
    /// is not resolved again. Internal so that only QuoteVersion can make one.
    /// </summary>
    internal CostingLine CopyForRevision() => new()
    {
        LineType = LineType,
        MaterialPriceId = MaterialPriceId,
        RateItemId = RateItemId,
        Description = Description,
        ResolvedUnitPrice = ResolvedUnitPrice,
        PriceOrigin = PriceOrigin,
        Quantity = Quantity,
        SupplierDiscountPercent = SupplierDiscountPercent,
        OverriddenUnitPrice = OverriddenUnitPrice,
        IsQuantityOverridden = IsQuantityOverridden,
        IsBelowTheLine = IsBelowTheLine,
        Derivation = Derivation,
        DerivationFactor = DerivationFactor,
        SheetAreaM2 = SheetAreaM2,
        SortOrder = SortOrder
    };

    /// <summary>
    /// Sets a quantity that follows from the job rather than from the estimator.
    /// Used by the calculator before totals are computed. A quantity the estimator
    /// has typed over is left alone.
    /// </summary>
    public void SetDerivedQuantity(decimal quantity)
    {
        if (!IsDerived)
            throw new InvalidOperationException("An entered quantity is not derived.");
        if (IsQuantityOverridden)
            return;
        Quantity = quantity;
    }

    // The changes below are made through QuoteVersion, which refuses them once the
    // version is sealed. They are internal so that nothing outside the domain can
    // alter a line without that check.

    internal void ChangeQuantity(decimal quantity)
    {
        GuardQuantity(quantity);
        Quantity = quantity;
        if (IsDerived)
            IsQuantityOverridden = true;
    }

    internal void RestoreDerivedQuantity()
    {
        if (!IsDerived)
            throw new InvalidOperationException($"{Description} has no derived quantity to restore.");
        IsQuantityOverridden = false;
    }

    internal void OverrideUnitPrice(decimal unitPrice)
    {
        // A rate typed on the quote is held to the same rule as a catalogue price
        // (team decision, 3 October 2026): an override of zero is refused.
        if (unitPrice <= 0)
            throw new ArgumentOutOfRangeException(nameof(unitPrice), "A rate typed on the quote must be greater than zero.");
        OverriddenUnitPrice = unitPrice == ResolvedUnitPrice ? null : unitPrice;
    }

    internal void ClearPriceOverride() => OverriddenUnitPrice = null;

    internal void ChangeSupplierDiscount(decimal supplierDiscountPercent)
    {
        if (LineType != CostingLineType.Material)
            throw new InvalidOperationException("A supplier discount applies to a material line only.");
        GuardDiscount(supplierDiscountPercent);
        SupplierDiscountPercent = supplierDiscountPercent;
    }

    private static void GuardQuantity(decimal quantity)
    {
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A quantity cannot be negative.");
    }

    private static void GuardDiscount(decimal supplierDiscountPercent)
    {
        if (supplierDiscountPercent is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(supplierDiscountPercent), "A discount must be between 0 and 100 per cent.");
    }
}
