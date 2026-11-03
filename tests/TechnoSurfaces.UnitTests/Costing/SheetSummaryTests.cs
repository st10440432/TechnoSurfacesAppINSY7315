using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Costing;

/// <summary>
/// The analysis of sheets and adhesive the client draws up for a tender, and pricing
/// an item per square metre. The first test is two rows of the client's George
/// Medi-Clinic summary: item 1, and item 67, needed twice, which the client counts as
/// 2 x 1,25 sheets and 2 x 8,5 seam kits.
/// </summary>
public sealed class SheetSummaryTests
{
    private const string OceanView = "Samsung Ocean View 3680 x 760";
    private const string Terrazzo = "Samsung Terrazzo Venezia 3680 x 760";

    private static CostingLine Sheet(string material, decimal pricePerSheet, decimal sheets, decimal discount = 0m) =>
        CostingLine.ForMaterial(1, material, pricePerSheet, "Price list", sheets, 2.78008m, discount);

    private static CostingLine SeamKits(decimal count, decimal price = 120m) =>
        CostingLine.ForRate(1, "Seamkit", price, "Rate card", count, false);

    private static CostingLine Rate(string name, decimal price, decimal quantity, bool belowTheLine = false) =>
        CostingLine.ForRate(1, name, price, "Rate card", quantity, belowTheLine);

    private static QuoteVersion TenderItemsOneAndSixtySeven()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 47m);
        var one = version.Items[0];
        version.ChangeItem(one, "Theatre complex 2: recovery: nurses station (8130)", "1", 1m);
        version.AddCostingLine(Sheet(OceanView, 7719m, 1.5m), one);
        version.AddCostingLine(SeamKits(3.5m), one);
        version.AddCostingLine(Rate("Sanding time", 100m, 2m), one);

        var sixtySeven = version.AddItem("Learning and development: boardroom lobby", "67", quantity: 2m);
        version.AddCostingLine(Sheet(OceanView, 7719m, 1.25m), sixtySeven);
        version.AddCostingLine(SeamKits(8.5m), sixtySeven);
        return version;
    }

    [Fact]
    public void Each_items_sheets_and_seam_kits_are_counted_as_often_as_it_is_needed()
    {
        var summary = SheetsAndAdhesive.Summarise(TenderItemsOneAndSixtySeven());

        var ocean = Assert.Single(summary.Materials);
        Assert.Equal(OceanView, ocean.Material);
        Assert.Equal(new[] { (1.5m, 3.5m), (2.5m, 17m) }, summary.Items.Select(i => (i.Sheets[0], i.SeamKits[0])));
        Assert.Equal((4m, 0m, 4m, 7719m, 30876m), (ocean.Sheets, ocean.BufferSheets, ocean.SheetsToOrder, ocean.CostPerSheet, ocean.SheetCost));
        Assert.Equal((20.5m, 120m, 2460m), (ocean.SeamKits, ocean.CostPerSeamKit, ocean.SeamKitCost));
        Assert.Equal(33336m, summary.TotalPurchaseCost);
    }

    [Fact]
    public void Buffer_stock_is_ordered_and_bought_at_the_cost_of_a_sheet()
    {
        var version = TenderItemsOneAndSixtySeven();

        version.SetBufferStock(OceanView, 5m);
        var ocean = SheetsAndAdhesive.Summarise(version).Materials[0];

        Assert.Equal((5m, 9m, 69471m), (ocean.BufferSheets, ocean.SheetsToOrder, ocean.SheetCost));

        Assert.NotNull(version.SetBufferStock(OceanView, 0m));
        Assert.Empty(version.BufferStock);
        Assert.Throws<ArgumentOutOfRangeException>(() => version.SetBufferStock(OceanView, -1m));
    }

    [Fact]
    public void Each_material_is_its_own_column_and_the_seam_kits_of_a_mixed_item_are_not_split()
    {
        var version = TenderItemsOneAndSixtySeven();
        var planters = version.AddItem("Planters", "79");
        version.AddCostingLine(Sheet(Terrazzo, 6581m, 8.5m), planters);
        version.AddCostingLine(Sheet(OceanView, 7719m, 1m), planters);
        version.AddCostingLine(SeamKits(40m), planters);

        var summary = SheetsAndAdhesive.Summarise(version);

        Assert.Equal(new[] { OceanView, Terrazzo }, summary.Materials.Select(m => m.Material));
        Assert.Equal(new[] { 1m, 8.5m }, summary.Items[2].Sheets);
        Assert.Equal(new[] { 0m, 0m }, summary.Items[2].SeamKits);
        Assert.Equal((40m, 4800m), (summary.UnsplitSeamKits, summary.UnsplitSeamKitCost));
        Assert.Equal(20.5m, summary.Materials[0].SeamKits);
    }

    [Fact]
    public void A_sheet_costs_what_the_costing_pays_for_it_after_the_supplier_discount()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 40m);
        version.AddCostingLine(Sheet(OceanView, 8000m, 1m, discount: 10m));
        var vanity = version.AddItem("Vanity");
        version.AddCostingLine(Sheet(OceanView, 8000m, 1m), vanity);

        var ocean = SheetsAndAdhesive.Summarise(version).Materials[0];

        // R7 200 and R8 000: R7 600 a sheet on average, R15 200 for the two.
        Assert.Equal((7600m, 15200m), (ocean.CostPerSheet, ocean.SheetCost));
    }

    [Fact]
    public void An_item_priced_per_square_metre_is_charged_its_area_at_that_price()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 47m);
        var item = version.Items[0];
        version.AddCostingLine(Sheet(OceanView, 7719.17m, 1.6m), item);
        version.AddCostingLine(Rate("Cut-out", 100m, 1m, belowTheLine: true), item);

        version.SetItemPricePerM2(item, 5000m);
        var totals = new QuoteCalculationService().Calculate(version);
        var figures = totals.Items[0];

        // 1,6 sheets of 2,78008 m² are 4,4481 m²: R22 240,50 at R5 000, then the cut-out at cost.
        Assert.Equal(22240.50m - 12350.67m, figures.MarkupAmount);
        Assert.Equal(22340.50m, figures.TotalExVat);
        Assert.Equal(5000m, figures.PricePerM2);

        version.SetItemPricePerM2(item, null);
        Assert.Equal(decimal.Round(12350.67m * 1.47m, 2, MidpointRounding.AwayFromZero) + 100m,
            new QuoteCalculationService().Calculate(version).Items[0].TotalExVat);
    }

    [Fact]
    public void A_revision_keeps_the_buffer_stock_and_each_items_price_per_square_metre()
    {
        var first = TenderItemsOneAndSixtySeven();
        first.SetBufferStock(OceanView, 5m);
        first.SetItemPricePerM2(first.Items[1], 4200m);

        var second = first.CreateRevision(2, "estimator");

        Assert.Equal((OceanView, 5m), (Assert.Single(second.BufferStock).Material, second.BufferStock.Single().Sheets));
        Assert.Equal(new decimal?[] { null, 4200m }, second.Items.Select(i => i.PricePerM2));
    }
}
