using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Costing;

/// <summary>
/// US-06: an estimator can override any rate or quantity on a quotation without
/// altering the rate card. Also covers silicon at two per sheet with an override,
/// and transport as the amount typed per job.
/// </summary>
public class CostingOverrideTests
{
    private readonly QuoteCalculationService _calculator = new();

    private static QuoteVersion NewVersion(decimal markupPercent = 0m) =>
        new(versionNo: 1, createdByUserId: "estimator", markupPercent: markupPercent);

    private static CostingLine Sheets(decimal quantity, decimal unitPrice = 4335.04m) =>
        CostingLine.ForMaterial(1, "Staron Bright White 3680 x 760", unitPrice,
            "Staron (Salvocorp) price band Bright White, 3680 x 760, effective 2025-03-01",
            quantity, sheetAreaM2: 2.7968m);

    private static CostingLine Fabrication(decimal hours) =>
        CostingLine.ForRate(1, "Fabrication — no backsplash, normal", 265m,
            "Rate card: Fabrication — no backsplash, normal, all suppliers, effective 2026-01-01",
            hours, isBelowTheLine: false);

    private static CostingLine Silicon() =>
        CostingLine.ForRate(2, "Silicon + sealing", 55m, "Rate card: Silicon + sealing", 0m,
            isBelowTheLine: false, DerivationRule.FromSheetCount, derivationFactor: 2m);

    private static CostingLine HobCutOut(decimal count) =>
        CostingLine.ForRate(3, "Hob cut out", 350m, "Rate card: Hob cut out", count, isBelowTheLine: true);

    // --------------------------------------------------------- rate override

    [Fact]
    public void A_rate_typed_on_the_quote_replaces_the_card_rate_on_that_line_only()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 10m);
        version.AddCostingLine(line);

        version.OverrideUnitPrice(line, 300m);

        Assert.Equal(3000.00m, line.LineTotal());
        Assert.Equal(265m, line.ResolvedUnitPrice);    // the card rate is kept beside it
        Assert.True(line.HasPriceOverride);
    }

    [Fact]
    public void Clearing_an_override_returns_the_line_to_the_card_rate()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 10m);
        version.AddCostingLine(line);
        version.OverrideUnitPrice(line, 300m);

        version.ClearPriceOverride(line);

        Assert.Equal(2650.00m, line.LineTotal());
        Assert.False(line.HasPriceOverride);
    }

    [Fact]
    public void Typing_the_card_rate_back_in_is_not_recorded_as_an_override()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 1m);
        version.AddCostingLine(line);

        version.OverrideUnitPrice(line, 265m);

        Assert.False(line.HasPriceOverride);
    }

    [Fact]
    public void A_supplier_discount_applies_to_an_overridden_material_price()
    {
        var version = NewVersion();
        var line = Sheets(quantity: 1m);
        version.AddCostingLine(line);

        version.OverrideUnitPrice(line, 4000m);
        version.ChangeSupplierDiscount(line, 10m);

        Assert.Equal(3600.00m, line.LineTotal());
    }

    [Fact]
    public void A_supplier_discount_cannot_be_put_on_a_rate_line()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 1m);
        version.AddCostingLine(line);

        Assert.Throws<InvalidOperationException>(() => version.ChangeSupplierDiscount(line, 10m));
    }

    // ---------------------------------------------------- quantity override

    [Fact]
    public void An_entered_quantity_can_be_changed()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 10m);
        version.AddCostingLine(line);

        version.ChangeQuantity(line, 12.5m);

        Assert.Equal(12.5m, line.Quantity);
        Assert.False(line.IsQuantityOverridden);
    }

    [Fact]
    public void Silicon_is_two_per_sheet_until_the_estimator_types_over_it()
    {
        var version = NewVersion();
        version.AddCostingLine(Sheets(quantity: 3m));
        var silicon = Silicon();
        version.AddCostingLine(silicon);

        _calculator.Calculate(version);
        Assert.Equal(6m, silicon.Quantity);

        version.ChangeQuantity(silicon, 9m);
        _calculator.Calculate(version);

        Assert.Equal(9m, silicon.Quantity);
        Assert.True(silicon.IsQuantityOverridden);
    }

    [Fact]
    public void Restoring_a_derived_quantity_follows_the_sheet_count_again()
    {
        var version = NewVersion();
        version.AddCostingLine(Sheets(quantity: 3m));
        var silicon = Silicon();
        version.AddCostingLine(silicon);
        version.ChangeQuantity(silicon, 9m);

        version.RestoreDerivedQuantity(silicon);
        _calculator.Calculate(version);

        Assert.Equal(6m, silicon.Quantity);
        Assert.False(silicon.IsQuantityOverridden);
    }

    // ------------------------------------------------------------- transport

    [Fact]
    public void Transport_is_added_below_the_line_and_is_not_marked_up()
    {
        var version = NewVersion(markupPercent: 47m);
        version.AddCostingLine(Sheets(quantity: 1m, unitPrice: 1000m));
        version.AddCostingLine(HobCutOut(count: 1m));
        version.SetTransportAmount(550m);

        var totals = _calculator.Calculate(version);

        Assert.Equal(1000.00m, totals.SubTotalExVat);
        Assert.Equal(470.00m, totals.MarkupAmount);         // 47% of the sub-total only
        Assert.Equal(550.00m, totals.TransportAmount);
        Assert.Equal(900.00m, totals.BelowTheLineTotal);    // 350 cut-out + 550 transport
        Assert.Equal(2370.00m, totals.TotalExVat);
        Assert.Equal(355.50m, totals.VatAmount);
    }

    [Fact]
    public void A_negative_transport_amount_is_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewVersion().SetTransportAmount(-1m));
    }

    // ------------------------------------------------------------ sealing

    [Fact]
    public void A_sealed_version_refuses_every_change()
    {
        var version = NewVersion();
        var material = Sheets(quantity: 1m);
        var silicon = Silicon();
        version.AddCostingLine(material);
        version.AddCostingLine(silicon);
        version.Seal();

        Assert.Throws<InvalidOperationException>(() => version.OverrideUnitPrice(material, 1m));
        Assert.Throws<InvalidOperationException>(() => version.ClearPriceOverride(material));
        Assert.Throws<InvalidOperationException>(() => version.ChangeQuantity(material, 2m));
        Assert.Throws<InvalidOperationException>(() => version.ChangeSupplierDiscount(material, 5m));
        Assert.Throws<InvalidOperationException>(() => version.RestoreDerivedQuantity(silicon));
        Assert.Throws<InvalidOperationException>(() => version.SetTransportAmount(100m));
    }

    [Fact]
    public void Totalling_a_sealed_version_does_not_derive_its_quantities_again()
    {
        // The issued figures are the record. A sealed version is read as it stands.
        var version = NewVersion();
        version.AddCostingLine(Sheets(quantity: 3m));
        var silicon = CostingLine.ForRate(2, "Silicon + sealing", 55m, "Rate card: Silicon + sealing", 7m,
            isBelowTheLine: false, DerivationRule.FromSheetCount, derivationFactor: 2m);
        version.AddCostingLine(silicon);
        version.Seal();

        _calculator.Calculate(version);

        Assert.Equal(7m, silicon.Quantity);
    }

    [Fact]
    public void A_line_from_another_version_cannot_be_changed_through_this_one()
    {
        var version = NewVersion();
        var stranger = Fabrication(hours: 1m);

        Assert.Throws<InvalidOperationException>(() => version.ChangeQuantity(stranger, 2m));
    }

    [Fact]
    public void A_negative_override_is_refused()
    {
        var version = NewVersion();
        var line = Fabrication(hours: 1m);
        version.AddCostingLine(line);

        Assert.Throws<ArgumentOutOfRangeException>(() => version.OverrideUnitPrice(line, -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => version.ChangeQuantity(line, -1m));
    }

    [Fact]
    public void An_override_of_zero_is_refused_and_the_line_keeps_its_rate()
    {
        var version = NewVersion();
        var labour = Fabrication(hours: 1m);
        var material = Sheets(quantity: 1m);
        version.AddCostingLine(labour);
        version.AddCostingLine(material);

        Assert.Throws<ArgumentOutOfRangeException>(() => version.OverrideUnitPrice(labour, 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => version.OverrideUnitPrice(material, 0m));

        Assert.False(labour.HasPriceOverride);
        Assert.Equal(labour.ResolvedUnitPrice, labour.UnitPrice);
        Assert.False(material.HasPriceOverride);
    }
}
