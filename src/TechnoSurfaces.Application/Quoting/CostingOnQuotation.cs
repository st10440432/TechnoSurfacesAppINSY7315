using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// What the costing sheet comes to on the customer quotation, and the rule that keeps
/// the lines copied from it in step: each material at its selling price, and one
/// amount for everything else. Used when the lines are copied, and by the costing
/// sheet after every change, so a copied line always shows the current price.
///
/// Only lines copied from the costing follow it. A line written by hand is never
/// changed, and a new material is not added until the estimator asks for it.
/// </summary>
internal static class CostingOnQuotation
{
    /// <summary>The selling price of each material, in costing order, and of the rest.</summary>
    internal sealed record SellingPrices(IReadOnlyList<(CostingLine Line, decimal Amount)> Materials, decimal OtherCosts, bool HasOtherCosts);

    internal static SellingPrices Price(QuoteVersion version, QuoteTotals totals)
    {
        var materials = version.CostingLines
            .Where(l => l.LineType == CostingLineType.Material && l.LineTotal() > 0)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
            .ToList();
        var hasOtherCosts = version.TransportAmount > 0
            || version.CostingLines.Any(l => l.LineType == CostingLineType.Rate && l.LineTotal() > 0);

        // A material is shown at its selling price: its cost with its item's markup,
        // as many times as the item is needed. The markup is never shown on its own
        // (US-11).
        var amounts = materials.Select(l =>
        {
            var item = version.ItemOf(l);
            return decimal.Round(l.LineTotal() * (1m + item.MarkupPercent / 100m) * item.Quantity, 2, MidpointRounding.AwayFromZero);
        }).ToList();

        // The rest is the difference to the costing total, so the lines add up to it
        // to the cent: the marked-up labour and consumables, the cut-outs and grooves
        // at cost, and transport. With no such costs, the cents lost to rounding each
        // material go on the last material instead.
        var remainder = totals.TotalExVat - amounts.Sum();
        if (!hasOtherCosts && amounts.Count > 0)
        {
            amounts[^1] += remainder;
            remainder = 0m;
        }

        return new SellingPrices(materials.Zip(amounts).ToList(), remainder, hasOtherCosts);
    }

    /// <summary>The quantity a copied material line gives: its sheets for every one of its item, or one item.</summary>
    internal static decimal Quantity(QuoteVersion version, CostingLine material) =>
        version.ShowSheetCounts ? material.SheetCount() * version.ItemOf(material).Quantity : 1m;

    /// <summary>
    /// Brings every copied line up to date with the costing. A line whose material has
    /// gone from the costing sheet goes too, as does the line for the rest when nothing
    /// else is left. Returns the lines taken off, for the caller to delete.
    /// </summary>
    internal static IReadOnlyList<QuotationLine> Follow(QuoteVersion version, QuoteTotals totals)
    {
        var prices = Price(version, totals);
        var amounts = prices.Materials.ToDictionary(m => m.Line, m => m.Amount);
        var removed = new List<QuotationLine>();

        foreach (var line in version.QuotationLines.ToList())
        {
            if (line.CostingLine is { } source)
            {
                if (amounts.TryGetValue(source, out var amount))
                    version.FollowCosting(line, amount, Quantity(version, source));
                else
                    removed.Add(line);
            }
            else if (line.CarriesOtherCosts)
            {
                if (prices.HasOtherCosts && prices.OtherCosts > 0)
                    version.FollowCosting(line, prices.OtherCosts, line.Quantity);
                else
                    removed.Add(line);
            }
        }

        foreach (var line in removed)
            version.RemoveQuotationLine(line);
        return removed;
    }
}
