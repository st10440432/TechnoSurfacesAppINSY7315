using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Costing;

/// <summary>
/// The business case for this system rests on the calculation. An untested
/// calculation engine would reintroduce precisely the silent pricing errors the
/// system exists to remove, so these tests assert the arithmetic the client
/// confirmed, line by line.
/// </summary>
public class QuoteCalculationTests
{
    private readonly QuoteCalculationService _calculator = new();

    private static QuoteVersion NewVersion(decimal markupPercent = 0m) =>
        new(versionNo: 1, createdByUserId: "test-user", markupPercent: markupPercent);

    private static CostingLine Material(decimal unitPrice, decimal quantity, decimal discount = 0m, decimal sheetArea = 2.7968m) =>
        CostingLine.ForMaterial(
            materialPriceId: 1,
            description: "Staron Bright White 3680 x 760",
            resolvedUnitPrice: unitPrice,
            priceOrigin: "Staron price band Bright White, 3680 x 760, effective 2025-03-01",
            quantity: quantity,
            sheetAreaM2: sheetArea,
            supplierDiscountPercent: discount);

    private static CostingLine Rate(decimal unitPrice, decimal quantity, bool belowTheLine = false,
        DerivationRule derivation = DerivationRule.Entered, string description = "Fabrication",
        decimal derivationFactor = 1m) =>
        CostingLine.ForRate(
            rateItemId: 1,
            description: description,
            resolvedUnitPrice: unitPrice,
            priceOrigin: "Rate card, effective 2026-01-01",
            quantity: quantity,
            isBelowTheLine: belowTheLine,
            derivation: derivation,
            derivationFactor: derivationFactor);

    // ------------------------------------------------------------ NFR-01, US-03

    [Fact]
    public void A_line_whose_price_never_resolved_blocks_the_calculation()
    {
        // The single most important test in the suite. The spreadsheet returns a
        // plausible figure when a lookup fails; this system must refuse instead.
        // The line carries a plausible figure on purpose: a line marked unresolved
        // blocks the calculation whatever number it holds.
        var version = NewVersion();
        version.AddCostingLine(CostingLine.ForRate(
            rateItemId: 1, description: "Fabrication", resolvedUnitPrice: 265m,
            priceOrigin: PriceResolution.UnresolvedOrigin, quantity: 10m, isBelowTheLine: false));

        var ex = Assert.Throws<PriceNotResolvedException>(() => _calculator.Calculate(version));
        Assert.Contains("Fabrication", ex.Message);
    }

    [Fact]
    public void A_line_cannot_be_priced_at_zero()
    {
        // The other half of NFR-01: a price of zero is refused before a line
        // exists, so it can never reach the totals.
        Assert.Throws<ArgumentOutOfRangeException>(() => CostingLine.ForRate(
            rateItemId: 1, description: "Fabrication", resolvedUnitPrice: 0m,
            priceOrigin: "Rate card: Fabrication", quantity: 10m, isBelowTheLine: false));

        Assert.Throws<ArgumentOutOfRangeException>(() => CostingLine.ForMaterial(
            materialPriceId: 1, description: "Staron Bright White", resolvedUnitPrice: 0m,
            priceOrigin: "Staron price band Bright White", quantity: 1m, sheetAreaM2: 2.7968m));
    }

    [Fact]
    public void A_priced_line_must_record_where_its_price_came_from()
    {
        // NFR-01 requires the origin to be visible, not just the figure, so a line
        // cannot be constructed without one.
        Assert.Throws<ArgumentException>(() => CostingLine.ForRate(
            rateItemId: 1, description: "Fabrication", resolvedUnitPrice: 450m,
            priceOrigin: "", quantity: 1m, isBelowTheLine: false));
    }

    [Fact]
    public void An_unresolved_price_cannot_be_read_as_zero()
    {
        var resolution = PriceResolution.Failure("No price is in force on 2026-09-29.");

        Assert.False(resolution.Resolved);
        Assert.Throws<PriceNotResolvedException>(() => resolution.UnitPrice);
    }

    // ------------------------------------------------------------------- US-08

    [Fact]
    public void Supplier_discount_reduces_the_unit_cost_before_markup()
    {
        var line = Material(unitPrice: 6300m, quantity: 1m, discount: 10m);

        Assert.Equal(5670.00m, line.EffectiveUnitPrice());
        Assert.Equal(5670.00m, line.LineTotal());
    }

    // ------------------------------------------------------------------- US-04

    [Fact]
    public void A_line_total_is_the_effective_unit_price_times_the_quantity()
    {
        var line = Material(unitPrice: 4335.04m, quantity: 3m);

        Assert.Equal(13005.12m, line.LineTotal());
    }

    // -------------------------------------------------------- markup boundary

    [Fact]
    public void Markup_applies_to_the_subtotal_only_and_never_to_below_the_line_items()
    {
        // Confirmed by the client: markup is calculated on the sub-total, and the
        // items below that line are principally a recovery of cost.
        var version = NewVersion(markupPercent: 25m);
        version.AddCostingLine(Material(unitPrice: 4000m, quantity: 1m));   // above
        version.AddCostingLine(Rate(unitPrice: 1000m, quantity: 1m, belowTheLine: true));

        var totals = _calculator.Calculate(version);

        Assert.Equal(4000.00m, totals.SubTotalExVat);
        Assert.Equal(1000.00m, totals.MarkupAmount);       // 25% of 4000, not of 5000
        Assert.Equal(1000.00m, totals.BelowTheLineTotal);
        Assert.Equal(6000.00m, totals.TotalExVat);
    }

    [Fact]
    public void Vat_is_applied_to_the_total_after_markup()
    {
        var version = NewVersion(markupPercent: 20m);
        version.AddCostingLine(Material(unitPrice: 5000m, quantity: 2m));

        var totals = _calculator.Calculate(version);

        Assert.Equal(10000.00m, totals.SubTotalExVat);
        Assert.Equal(2000.00m, totals.MarkupAmount);
        Assert.Equal(12000.00m, totals.TotalExVat);
        Assert.Equal(1800.00m, totals.VatAmount);          // 15% of 12000
        Assert.Equal(13800.00m, totals.TotalIncVat);
    }

    // ------------------------------------------------------- derived quantities

    [Fact]
    public void Consumables_take_their_quantity_from_the_total_area()
    {
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 4335.04m, quantity: 3m, sheetArea: 2.7968m));
        var consumables = Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromTotalAreaM2, description: "Sandpaper & consumables");
        version.AddCostingLine(consumables);

        var totals = _calculator.Calculate(version);

        Assert.Equal(8.3904m, totals.TotalAreaM2);         // 3 sheets x 2,7968
        Assert.Equal(9m, consumables.Quantity);            // rounded up to a whole m²
    }

    [Theory]
    [InlineData(19.5776, 20)]   // the costing sheet that prompted the rule
    [InlineData(8.0001, 9)]     // any part of a unit counts as a whole one
    [InlineData(12, 12)]        // a whole figure is left as it is
    public void Sandpaper_and_consumables_round_up_to_a_whole_square_metre(decimal area, decimal expected)
    {
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 1000m, quantity: 1m, sheetArea: area));
        var consumables = Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromTotalAreaM2, description: "Sandpaper & consumables");
        version.AddCostingLine(consumables);

        var totals = _calculator.Calculate(version);

        Assert.Equal(area, totals.TotalAreaM2);             // the area itself is not rounded
        Assert.Equal(expected, consumables.Quantity);
    }

    [Fact]
    public void Silicon_rounds_up_to_a_whole_tube()
    {
        // 2,75 sheets at two per sheet is 5,5 tubes, charged as 6.
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 1000m, quantity: 2.75m));
        var silicon = Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromSheetCount, description: "Silicon + sealing", derivationFactor: 2m);
        version.AddCostingLine(silicon);

        _calculator.Calculate(version);

        Assert.Equal(6m, silicon.Quantity);
        Assert.Equal(330.00m, silicon.LineTotal());
    }

    [Fact]
    public void A_rounded_quantity_the_estimator_typed_over_is_left_as_typed()
    {
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 1000m, quantity: 1m, sheetArea: 2.7968m));
        var consumables = Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromTotalAreaM2, description: "Sandpaper & consumables");
        version.AddCostingLine(consumables);
        _calculator.Calculate(version);

        version.ChangeQuantity(consumables, 2.5m);
        _calculator.Calculate(version);

        Assert.Equal(2.5m, consumables.Quantity);
    }

    [Fact]
    public void Silicon_takes_two_per_sheet_from_the_sheet_count()
    {
        // The client's costing sheet labels the line "silicon (2/sheet) + sealing".
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 4335.04m, quantity: 2.5m));
        var silicon = Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromSheetCount, description: "Silicon + sealing", derivationFactor: 2m);
        version.AddCostingLine(silicon);

        _calculator.Calculate(version);

        Assert.Equal(5m, silicon.Quantity);
        Assert.Equal(275.00m, silicon.LineTotal());
    }

    [Fact]
    public void Consumables_are_inside_the_sub_total_and_marked_up()
    {
        // Workbook: SUB TOTAL is the sum of every line above it, consumables
        // included, so the markup applies to them.
        var version = NewVersion(markupPercent: 10m);
        version.AddCostingLine(Material(unitPrice: 1000m, quantity: 1m, sheetArea: 2m));
        version.AddCostingLine(Rate(unitPrice: 55m, quantity: 0m,
            derivation: DerivationRule.FromTotalAreaM2, description: "Sandpaper & consumables"));

        var totals = _calculator.Calculate(version);

        Assert.Equal(1110.00m, totals.SubTotalExVat);   // 1000 + 2 m2 x 55
        Assert.Equal(111.00m, totals.MarkupAmount);
    }

    [Fact]
    public void A_quantity_the_estimator_entered_is_never_overwritten_by_a_derivation()
    {
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 4335.04m, quantity: 5m));
        var entered = Rate(unitPrice: 450m, quantity: 12m);
        version.AddCostingLine(entered);

        _calculator.Calculate(version);

        Assert.Equal(12m, entered.Quantity);
    }

    // ------------------------------------------------------------------- US-22

    [Fact]
    public void A_line_exposes_no_way_to_change_the_price_it_was_created_with()
    {
        // The copied price has no public setter, so nothing outside the domain can
        // move an existing quote's figures. The end-to-end behaviour, with a real
        // catalogue price changing underneath a saved line, is covered in
        // PriceSnapshotTests.
        var property = typeof(CostingLine).GetProperty(nameof(CostingLine.ResolvedUnitPrice));

        Assert.NotNull(property);
        Assert.Null(property!.SetMethod?.IsPublic == true ? property.SetMethod : null);
    }

    // ---------------------------------------------------------------- rounding

    [Fact]
    public void A_multi_line_quote_totals_to_the_cent()
    {
        var version = NewVersion(markupPercent: 17.5m);
        version.AddCostingLine(Material(unitPrice: 4335.04m, quantity: 1.5m));
        version.AddCostingLine(Material(unitPrice: 5257.98m, quantity: 0.5m));
        version.AddCostingLine(Rate(unitPrice: 487.33m, quantity: 7m));

        var totals = _calculator.Calculate(version);

        var expectedSubTotal =
            decimal.Round(4335.04m * 1.5m, 2) +
            decimal.Round(5257.98m * 0.5m, 2) +
            decimal.Round(487.33m * 7m, 2);

        Assert.Equal(expectedSubTotal, totals.SubTotalExVat);
        Assert.Equal(decimal.Round(expectedSubTotal * 0.175m, 2), totals.MarkupAmount);
        Assert.Equal(totals.SubTotalExVat + totals.MarkupAmount, totals.TotalExVat);
        Assert.Equal(decimal.Round(totals.TotalExVat * 0.15m, 2), totals.VatAmount);
        Assert.Equal(totals.TotalExVat + totals.VatAmount, totals.TotalIncVat);
    }

    [Fact]
    public void Fractional_sheet_quantities_are_supported()
    {
        // Woodcentre quote stock in half sheets, so a sheet count is not an integer.
        var version = NewVersion();
        version.AddCostingLine(Material(unitPrice: 3900m, quantity: 0.5m));

        var totals = _calculator.Calculate(version);

        Assert.Equal(1950.00m, totals.SubTotalExVat);
    }

    // ------------------------------------------------------ guards on the input

    [Fact]
    public void A_negative_quantity_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Material(unitPrice: 100m, quantity: -1m));
    }

    [Fact]
    public void A_discount_outside_nought_to_one_hundred_per_cent_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Material(unitPrice: 100m, quantity: 1m, discount: 120m));
    }
}
