using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.UnitTests.Costing;

/// <summary>
/// A quote made of items, each costed like one page of the client's tender costing:
/// its own lines, its own markup, its own sandpaper and silicon, and its total for one
/// counted as many times as the job needs it. The first test is item 1 of the
/// client's George Medi-Clinic tender, which the client priced at R21 846,58.
/// </summary>
public sealed class QuoteItemCalculationTests
{
    private readonly QuoteCalculationService _calculator = new();

    // Items are not saved in these tests, so they have no ids yet: totals are read by position.

    private static CostingLine Sheet(decimal pricePerSheet, decimal sheets, decimal sheetAreaM2 = 2.78008m) =>
        CostingLine.ForMaterial(1, "Samsung 12 mm, 3 680 x 760", pricePerSheet, "Price list", sheets, sheetAreaM2);

    private static CostingLine Rate(string name, decimal price, decimal quantity, bool belowTheLine = false,
        DerivationRule derivation = DerivationRule.Entered, decimal factor = 1m) =>
        CostingLine.ForRate(1, name, price, "Rate card", quantity, belowTheLine, derivation, factor);

    /// <summary>The lines of the client's tender sheet "1", for one recovery nurses station.</summary>
    private static void AddTenderItemOne(QuoteVersion version, QuoteItem item)
    {
        version.AddCostingLine(Sheet(7719.17m, 1.6m), item);
        version.AddCostingLine(Rate("No backsplash, normal rate", 265m, 3m), item);
        version.AddCostingLine(Rate("Seam kit 50 ml", 120m, 3.5m), item);
        version.AddCostingLine(Rate("Sanding time", 100m, 2m), item);
        version.AddCostingLine(Rate("Sandpaper and other consumables", 55m, 0m, derivation: DerivationRule.FromTotalAreaM2), item);
        version.AddCostingLine(Rate("Installation, normal rate", 265m, 2.5m), item);
        version.AddCostingLine(Rate("Silicon and sealing", 59m, 0m, derivation: DerivationRule.FromSheetCount, factor: 2m), item);
    }

    [Fact]
    public void An_item_costs_what_the_clients_tender_sheet_costs_it_at()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 47m);
        var item = version.Items[0];
        AddTenderItemOne(version, item);

        var totals = _calculator.Calculate(version);
        var one = totals.Items[0];

        Assert.Equal(14861.62m, one.SubTotalExVat);
        Assert.Equal(6984.96m, one.MarkupAmount);
        Assert.Equal(21846.58m, one.TotalExVat);
        Assert.Equal(21846.58m, totals.TotalExVat);
    }

    [Fact]
    public void Each_item_is_marked_up_by_its_own_markup_and_counted_as_often_as_it_is_needed()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 50m);
        var kitchen = version.Items[0];
        version.AddCostingLine(Sheet(1000m, 1m), kitchen);
        var vanities = version.AddItem("Vanity", quantity: 3m, markupPercent: 20m);
        version.AddCostingLine(Sheet(500m, 1m), vanities);
        version.SetTransportAmount(250m);

        var totals = _calculator.Calculate(version);

        Assert.Equal(1500m, totals.Items[0].AmountExVat);
        Assert.Equal((600m, 1800m), (totals.Items[1].TotalExVat, totals.Items[1].AmountExVat));
        // Kitchen R1 500, three vanities R1 800, transport once.
        Assert.Equal(3550m, totals.TotalExVat);
        Assert.Equal(2500m, totals.SubTotalExVat);
        Assert.Equal(800m, totals.MarkupAmount);
    }

    [Fact]
    public void Sandpaper_and_silicon_follow_each_items_own_material()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 0m);
        var first = version.Items[0];
        var second = version.AddItem("Vanity");
        version.AddCostingLine(Sheet(1000m, 2m, sheetAreaM2: 2m), first);
        var firstSilicon = Rate("Silicon and sealing", 59m, 0m, derivation: DerivationRule.FromSheetCount, factor: 2m);
        version.AddCostingLine(firstSilicon, first);
        version.AddCostingLine(Sheet(1000m, 0.5m, sheetAreaM2: 2m), second);
        var secondSandpaper = Rate("Sandpaper", 55m, 0m, derivation: DerivationRule.FromTotalAreaM2);
        version.AddCostingLine(secondSandpaper, second);

        _calculator.Calculate(version);

        Assert.Equal(4m, firstSilicon.Quantity);
        Assert.Equal(1m, secondSandpaper.Quantity);
    }

    [Fact]
    public void A_quote_of_one_item_totals_as_it_did_before_items()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 35m);
        version.AddCostingLine(Sheet(1000m, 1m));
        version.AddCostingLine(Rate("Cut-out", 225m, 1m, belowTheLine: true));
        version.SetTransportAmount(100m);

        var totals = _calculator.Calculate(version);

        Assert.Single(totals.Items);
        Assert.Equal(1000m, totals.SubTotalExVat);
        Assert.Equal(35m, totals.MarkupPercent);
        Assert.Equal(325m, totals.BelowTheLineTotal);
        Assert.Equal(1675m, totals.TotalExVat);
    }

    [Fact]
    public void A_new_item_starts_at_the_markup_of_the_item_before_it()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 35m);
        version.SetItemMarkupPercent(version.Items[0], 47m);

        var added = version.AddItem("Vanity");

        Assert.Equal(47m, added.MarkupPercent);
        Assert.Equal(47m, version.MarkupPercent);
    }

    [Fact]
    public void The_quotes_markup_sets_every_item_and_an_items_markup_sets_only_that_item()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 35m);
        var first = version.Items[0];
        var second = version.AddItem("Vanity");

        version.SetMarkupPercent(40m);
        version.SetItemMarkupPercent(second, 20m);

        Assert.Equal((40m, 20m), (first.MarkupPercent, second.MarkupPercent));
    }

    [Fact]
    public void An_item_goes_with_its_lines_and_the_last_item_stays()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 35m);
        var vanity = version.AddItem("Vanity");
        var line = Sheet(500m, 1m);
        version.AddCostingLine(line, vanity);

        var removed = version.RemoveItem(vanity);

        Assert.Equal([line], removed);
        Assert.Empty(version.CostingLines);
        Assert.Throws<InvalidOperationException>(() => version.RemoveItem(version.Items[0]));
    }

    [Fact]
    public void A_revision_copies_every_item_with_its_own_lines()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 47m);
        AddTenderItemOne(version, version.Items[0]);
        var vanities = version.AddItem("Vanity", "2", quantity: 2m, markupPercent: 30m);
        version.AddCostingLine(Sheet(500m, 1m), vanities);
        var before = _calculator.Calculate(version);
        version.Seal();

        var revision = version.CreateRevision(2, "estimator");
        var after = _calculator.Calculate(revision);

        Assert.Equal(["Item 1", "Vanity"], revision.Items.Select(i => i.Name));
        Assert.Equal(("2", 2m, 30m), (revision.Items[1].Number, revision.Items[1].Quantity, revision.Items[1].MarkupPercent));
        Assert.Single(revision.LinesOf(revision.Items[1]));
        Assert.Equal(before.TotalExVat, after.TotalExVat);
        Assert.DoesNotContain(revision.Items, i => version.Items.Contains(i));
    }

    [Fact]
    public void An_item_needs_a_name_and_is_needed_at_least_once()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 35m);

        Assert.Throws<ArgumentException>(() => version.AddItem("  "));
        Assert.Throws<ArgumentOutOfRangeException>(() => version.AddItem("Vanity", quantity: 0m));
    }
}
