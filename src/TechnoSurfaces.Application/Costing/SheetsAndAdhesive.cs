using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Costing;

/// <summary>
/// The analysis of sheets and adhesive the client draws up for a tender: for every
/// item, the sheets of each material and the seam kits joining them, as many times
/// as the item is needed; then per material the sheets to order with the buffer
/// stock, the cost of a sheet, and what the order costs to buy.
///
/// Internal to Techno Surfaces: it carries cost prices. Read from the costing as it
/// stands, so nothing on it is typed but the buffer stock.
/// </summary>
public static class SheetsAndAdhesive
{
    public static SheetSummary Summarise(QuoteVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        // Each material is one column: the costing sheet's description names its
        // product line, colour and sheet size, so two sizes of a colour are two columns.
        var materials = version.Items
            .SelectMany(i => version.LinesOf(i).OrderBy(l => l.SortOrder).ThenBy(l => l.Id))
            .Where(l => l.LineType == CostingLineType.Material)
            .Select(l => l.Description)
            .Distinct()
            .ToList();

        var sheets = materials.ToDictionary(m => m, _ => (Count: 0m, Cost: 0m));
        var kits = materials.ToDictionary(m => m, _ => (Count: 0m, Cost: 0m));
        var unsplit = (Count: 0m, Cost: 0m);
        var rows = new List<SheetSummaryItem>();

        foreach (var item in version.Items)
        {
            var lines = version.LinesOf(item).ToList();
            var itemSheets = materials.ToDictionary(m => m, _ => 0m);
            foreach (var line in lines.Where(l => l.LineType == CostingLineType.Material))
            {
                itemSheets[line.Description] += line.SheetCount() * item.Quantity;
                var (count, cost) = sheets[line.Description];
                sheets[line.Description] = (count + line.SheetCount() * item.Quantity, cost + line.LineTotal() * item.Quantity);
            }

            // An item of one material uses its seam kits on that material. The seam kits
            // of an item of several materials cannot be told apart, so they are counted
            // on their own rather than divided by a guess.
            var used = lines.Where(l => l.LineType == CostingLineType.Material).Select(l => l.Description).Distinct().ToList();
            var itemKits = materials.ToDictionary(m => m, _ => 0m);
            var itemUnsplit = 0m;
            foreach (var kit in lines.Where(l => l.IsSeamKit))
            {
                var count = kit.Quantity * item.Quantity;
                var cost = kit.LineTotal() * item.Quantity;
                if (used.Count == 1)
                {
                    itemKits[used[0]] += count;
                    kits[used[0]] = (kits[used[0]].Count + count, kits[used[0]].Cost + cost);
                }
                else
                {
                    itemUnsplit += count;
                    unsplit = (unsplit.Count + count, unsplit.Cost + cost);
                }
            }

            rows.Add(new SheetSummaryItem(item.Id, item.Number, item.Name, item.Quantity,
                materials.Select(m => itemSheets[m]).ToList(), materials.Select(m => itemKits[m]).ToList(), itemUnsplit));
        }

        var columns = materials.Select(m =>
        {
            var (count, cost) = sheets[m];
            var perSheet = count > 0 ? Round(cost / count) : 0m;
            var buffer = version.BufferStock.FirstOrDefault(b => b.Material == m)?.Sheets ?? 0m;
            var (kitCount, kitCost) = kits[m];
            return new SheetSummaryMaterial(m, count, buffer, count + buffer, perSheet, Round(cost + buffer * perSheet),
                kitCount, kitCount > 0 ? Round(kitCost / kitCount) : 0m, Round(kitCost));
        }).ToList();

        return new SheetSummary(columns, rows, unsplit.Count, Round(unsplit.Cost),
            Round(columns.Sum(c => c.SheetCost + c.SeamKitCost) + unsplit.Cost));
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>The summary of sheets and adhesive. Internal: it carries cost prices.</summary>
/// <param name="UnsplitSeamKits">Seam kits of items made of more than one material, not given to any one material.</param>
public sealed record SheetSummary(
    IReadOnlyList<SheetSummaryMaterial> Materials,
    IReadOnlyList<SheetSummaryItem> Items,
    decimal UnsplitSeamKits,
    decimal UnsplitSeamKitCost,
    decimal TotalPurchaseCost);

/// <summary>
/// One material across the job. The cost of a sheet is what the costing pays for one,
/// after any supplier discount; with more than one price on the job, the average.
/// </summary>
public sealed record SheetSummaryMaterial(
    string Material,
    decimal Sheets,
    decimal BufferSheets,
    decimal SheetsToOrder,
    decimal CostPerSheet,
    decimal SheetCost,
    decimal SeamKits,
    decimal CostPerSeamKit,
    decimal SeamKitCost);

/// <summary>One item's sheets and seam kits per material, in the order of the materials, for every one needed.</summary>
public sealed record SheetSummaryItem(
    int ItemId,
    string? Number,
    string Name,
    decimal Quantity,
    IReadOnlyList<decimal> Sheets,
    IReadOnlyList<decimal> SeamKits,
    decimal UnsplitSeamKits);
