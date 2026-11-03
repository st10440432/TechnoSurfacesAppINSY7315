using System.Globalization;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// A material line: the colour and sheet size chosen in the cascade, for the item
/// named, or the quote's first item.
/// </summary>
public sealed record AddMaterialLine(int ColourId, int SheetSizeId, decimal Quantity, decimal SupplierDiscountPercent, int? ItemId = null);

/// <summary>
/// A line from the rate card. <paramref name="UnitPrice"/> is a price typed for this
/// job, accepted only for an item the rate card has no price for, such as the wood
/// boards, sinks and hardware the client prices per job. For the item named, or
/// the quote's first item.
/// </summary>
public sealed record AddRateLine(int RateItemId, decimal Quantity, decimal? UnitPrice = null, int? ItemId = null);

/// <summary>
/// Changes to one line on this quote only (US-04, US-06, US-08). Every field is
/// optional; only what is set is applied. The rate card is never touched.
/// </summary>
public sealed record ChangeLine(
    decimal? Quantity = null,
    decimal? UnitPrice = null,
    bool ClearPriceOverride = false,
    decimal? SupplierDiscountPercent = null,
    bool RestoreDerivedQuantity = false);

/// <summary>
/// The figures held on the quote rather than on a line. The markup is the named
/// item's, or every item's when none is named. Transport and the allowance are for
/// the whole job.
/// </summary>
public sealed record ChangeCosting(decimal? MarkupPercent = null, decimal? TransportAmount = null, int? ItemId = null,
    decimal? AllowanceAmount = null);

/// <summary>
/// An item on the quote: its name, its number on a bill of quantities, how many are
/// needed, and the unit and section it is listed under on the bill. A price per
/// square metre prices the item by the area of its material instead of by its
/// markup; none prices it by its markup.
/// </summary>
public sealed record ItemInput(string Name, string? Number, decimal Quantity, decimal? MarkupPercent = null,
    string? Unit = null, string? Section = null, decimal? PricePerM2 = null);

public enum CostingOutcome
{
    Ok,
    QuoteNotFound,
    LineNotFound,

    /// <summary>The quote exists but has no version with that number.</summary>
    VersionNotFound,

    /// <summary>NFR-01 and US-03. The line was not created; the reason says why.</summary>
    PriceNotResolved,

    /// <summary>A retired colour or rate item cannot go on a quote (US-24).</summary>
    NotSelectable,

    /// <summary>The current version is sealed. Reopen the quote to change it.</summary>
    VersionSealed,

    /// <summary>The change does not apply to this line, for example a supplier discount on a rate line.</summary>
    NotApplicable,

    /// <summary>The quote's current version has no item with that id.</summary>
    ItemNotFound
}

public sealed record CostingResult(
    CostingOutcome Outcome,
    Quote? Quote = null,
    CostingLine? Line = null,
    QuoteTotals? Totals = null,
    string? Problem = null,
    QuoteVersion? Version = null,
    QuoteItem? Item = null);

public interface ICostingSheetService
{
    /// <summary>The quote's current version with its lines and totals.</summary>
    Task<CostingResult> GetAsync(int quoteId, CancellationToken ct = default);

    /// <summary>
    /// Any version of the quote with its lines and totals, read only (US-21). An
    /// earlier version is sealed, so it is totalled exactly as it was issued.
    /// </summary>
    Task<CostingResult> GetVersionAsync(int quoteId, int versionNo, CancellationToken ct = default);

    Task<CostingResult> GetLineAsync(int quoteId, int lineId, CancellationToken ct = default);

    Task<CostingResult> AddMaterialLineAsync(int quoteId, AddMaterialLine request, CancellationToken ct = default);

    Task<CostingResult> AddRateLineAsync(int quoteId, AddRateLine request, CancellationToken ct = default);

    Task<CostingResult> ChangeLineAsync(int quoteId, int lineId, ChangeLine request, CancellationToken ct = default);

    Task<CostingResult> RemoveLineAsync(int quoteId, int lineId, CancellationToken ct = default);

    /// <summary>Markup (US-07) and the transport amount typed per job.</summary>
    Task<CostingResult> ChangeCostingAsync(int quoteId, ChangeCosting request, CancellationToken ct = default);

    /// <summary>A new item with an empty costing sheet, listed last.</summary>
    Task<CostingResult> AddItemAsync(int quoteId, ItemInput request, CancellationToken ct = default);

    /// <summary>Renames an item, or changes its number, how many are needed or its markup.</summary>
    Task<CostingResult> ChangeItemAsync(int quoteId, int itemId, ItemInput request, CancellationToken ct = default);

    /// <summary>Takes an item and its costing off the quote. The last item stays.</summary>
    Task<CostingResult> RemoveItemAsync(int quoteId, int itemId, CancellationToken ct = default);

    /// <summary>
    /// The summary of sheets and adhesive for the current version, or for the version
    /// named. Internal: it carries cost prices.
    /// </summary>
    Task<SheetSummary?> GetSheetSummaryAsync(int quoteId, int? versionNo = null, CancellationToken ct = default);

    /// <summary>The buffer stock of one material on the current version; no sheets takes it off.</summary>
    Task<CostingResult> SetBufferStockAsync(int quoteId, string material, decimal sheets, CancellationToken ct = default);
}

/// <summary>
/// The costing sheet: priced lines on the open version of a quote.
///
/// A line is created only from a resolved price. When the price cannot be resolved
/// nothing is added and nothing is saved; the caller gets the reason to show the
/// estimator. There is no path on which a line is priced at zero.
///
/// Who may edit a given quote is an authorisation rule and is checked by the caller
/// before any of these methods run. This service does not test roles.
/// </summary>
public sealed class CostingSheetService : ICostingSheetService
{
    /// <summary>The origin recorded on a line whose price the estimator typed for this job.</summary>
    public const string EnteredOnQuoteOrigin = "Price entered on the quote";

    private readonly IQuoteRepository _quotes;
    private readonly ICatalogueReader _catalogue;
    private readonly IPriceResolver _prices;
    private readonly IRateResolver _rates;
    private readonly IQuoteCalculationService _calculator;

    public CostingSheetService(
        IQuoteRepository quotes,
        ICatalogueReader catalogue,
        IPriceResolver prices,
        IRateResolver rates,
        IQuoteCalculationService calculator)
    {
        _quotes = quotes;
        _catalogue = catalogue;
        _prices = prices;
        _rates = rates;
        _calculator = calculator;
    }

    public async Task<CostingResult> GetAsync(int quoteId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);

        return new(CostingOutcome.Ok, quote, Totals: _calculator.Calculate(version), Version: version);
    }

    public async Task<CostingResult> GetVersionAsync(int quoteId, int versionNo, CancellationToken ct = default)
    {
        var quote = await _quotes.GetWithAllVersionsAsync(quoteId, ct);
        if (quote is null)
            return new(CostingOutcome.QuoteNotFound);
        if (quote.Version(versionNo) is not { } version)
            return new(CostingOutcome.VersionNotFound, quote, Problem: $"Quote {quote.Reference} has no version {versionNo}.");

        return new(CostingOutcome.Ok, quote, Totals: _calculator.Calculate(version), Version: version);
    }

    public async Task<CostingResult> GetLineAsync(int quoteId, int lineId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);

        var line = version.CostingLines.FirstOrDefault(l => l.Id == lineId);
        return line is null
            ? new(CostingOutcome.LineNotFound)
            : new(CostingOutcome.Ok, quote, line, _calculator.Calculate(version));
    }

    public async Task<CostingResult> AddMaterialLineAsync(int quoteId, AddMaterialLine request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

        var item = ItemOrFirst(version, request.ItemId);
        if (item is null)
            return ItemNotFound(request.ItemId);

        var colour = await _catalogue.GetColourAsync(request.ColourId, ct);
        if (colour is { IsSelectable: false })
            return new(CostingOutcome.NotSelectable,
                Problem: $"{colour.Name} has been discontinued and cannot be chosen on a new line.");

        // Priced as at the quote's issue date: the price valid when the quote was
        // created, not today's price.
        var resolution = await _prices.ResolveAsync(new PriceKey(request.ColourId, request.SheetSizeId), quote.IssueDate, ct);
        if (!resolution.Resolved)
            return new(CostingOutcome.PriceNotResolved, Problem: resolution.FailureReason);

        var size = await _catalogue.GetSheetSizeAsync(request.SheetSizeId, ct)
            ?? throw new InvalidOperationException($"Sheet size {request.SheetSizeId} resolved a price but could not be loaded.");

        var line = CostingLine.ForMaterial(
            materialPriceId: resolution.SourcePriceId
                ?? throw new InvalidOperationException("A resolved material price must name the price row it came from."),
            description: $"{colour!.ProductLine?.Name} {colour.Name} {size}",
            resolvedUnitPrice: resolution.UnitPrice,
            priceOrigin: resolution.Origin,
            quantity: request.Quantity,
            sheetAreaM2: size.AreaM2,
            supplierDiscountPercent: request.SupplierDiscountPercent);

        return await AddAsync(quote, version, item, line, ct);
    }

    public async Task<CostingResult> AddRateLineAsync(int quoteId, AddRateLine request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

        var quoteItem = ItemOrFirst(version, request.ItemId);
        if (quoteItem is null)
            return ItemNotFound(request.ItemId);

        var item = await _catalogue.GetRateItemAsync(request.RateItemId, ct);
        if (item is null)
            return new(CostingOutcome.PriceNotResolved, Problem: $"Rate item {request.RateItemId} is not on the rate card.");
        if (item.Status == CatalogueStatus.Discontinued)
            return new(CostingOutcome.NotSelectable, Problem: $"{item.Name} has been retired from the rate card.");

        // supplierId is null: the seeded rate card holds no supplier-specific rates.
        var resolution = await _rates.ResolveAsync(request.RateItemId, supplierId: null, quote.IssueDate, ct);

        decimal unitPrice;
        string origin;
        if (resolution.Resolved)
        {
            // An item with a rate-card price is charged at it. A different figure for
            // this job is an override on the line, which keeps the card price beside
            // it (US-06), not a price typed in its place.
            if (request.UnitPrice is not null)
                return new(CostingOutcome.NotApplicable,
                    Problem: $"{item.Name} has a rate-card price of R{resolution.UnitPrice.ToString("0.00", CultureInfo.InvariantCulture)}. " +
                             "Add the line without a price and override the rate on the line instead.");

            unitPrice = resolution.UnitPrice;
            origin = resolution.Origin;
        }
        else
        {
            // No price on the rate card. The estimator may type one for this job, and
            // the line says so; without one the line is refused, never priced at zero.
            if (request.UnitPrice is not { } typed)
                return new(CostingOutcome.PriceNotResolved,
                    Problem: $"{resolution.FailureReason} To add it to this quote, enter a price for this job.");

            unitPrice = typed;
            origin = EnteredOnQuoteOrigin;
        }

        var line = CostingLine.ForRate(
            item.Id, item.Name, unitPrice, origin, request.Quantity,
            item.IsBelowTheLine, item.Derivation, item.DerivationFactor);
        line.SortOrder = item.SortOrder;

        return await AddAsync(quote, version, quoteItem, line, ct);
    }

    public async Task<CostingResult> ChangeLineAsync(int quoteId, int lineId, ChangeLine request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);

        var line = version.CostingLines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return new(CostingOutcome.LineNotFound);
        if (version.IsSealed)
            return Sealed(version);

        if (request.SupplierDiscountPercent is not null && line.LineType != CostingLineType.Material)
            return new(CostingOutcome.NotApplicable, Problem: "A supplier discount applies to a material line only.");
        if (request.RestoreDerivedQuantity && !line.IsDerived)
            return new(CostingOutcome.NotApplicable, Problem: $"{line.Description} has no calculated quantity to restore.");

        if (request.SupplierDiscountPercent is { } newDiscount)
            version.ChangeSupplierDiscount(line, newDiscount);

        if (request.ClearPriceOverride)
            version.ClearPriceOverride(line);
        else if (request.UnitPrice is { } unitPrice)
            version.OverrideUnitPrice(line, unitPrice);

        if (request.RestoreDerivedQuantity)
            version.RestoreDerivedQuantity(line);
        else if (request.Quantity is { } quantity)
            version.ChangeQuantity(line, quantity);

        var totals = CalculateAndFollow(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, line, totals);
    }

    public async Task<CostingResult> RemoveLineAsync(int quoteId, int lineId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);

        var line = version.CostingLines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return new(CostingOutcome.LineNotFound);
        if (version.IsSealed)
            return Sealed(version);

        // The quotation line copied from this material goes with it, before the
        // material itself is deleted.
        version.RemoveCostingLine(line);
        var totals = CalculateAndFollow(version);
        _quotes.RemoveCostingLine(line);

        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: totals);
    }

    public async Task<CostingResult> ChangeCostingAsync(int quoteId, ChangeCosting request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

        QuoteItem? item = null;
        if (request.ItemId is { } itemId && (item = version.Items.FirstOrDefault(i => i.Id == itemId)) is null)
            return ItemNotFound(itemId);

        if (request.MarkupPercent is { } markup)
        {
            if (item is null)
                version.SetMarkupPercent(markup);
            else
                version.SetItemMarkupPercent(item, markup);
        }
        if (request.TransportAmount is { } transport)
            version.SetTransportAmount(transport);
        if (request.AllowanceAmount is { } allowance)
            version.SetAllowance(allowance, version.AllowanceDescription);

        var totals = CalculateAndFollow(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: totals);
    }

    public async Task<CostingResult> AddItemAsync(int quoteId, ItemInput request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

        var item = version.AddItem(request.Name, request.Number, request.Quantity, request.MarkupPercent, request.Unit, request.Section);
        version.SetItemPricePerM2(item, request.PricePerM2);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: _calculator.Calculate(version), Version: version, Item: item);
    }

    public async Task<CostingResult> ChangeItemAsync(int quoteId, int itemId, ItemInput request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);
        if (version.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
            return ItemNotFound(itemId);

        version.ChangeItem(item, request.Name, request.Number, request.Quantity, request.Unit, request.Section);
        version.SetItemPricePerM2(item, request.PricePerM2);
        if (request.MarkupPercent is { } markup)
            version.SetItemMarkupPercent(item, markup);

        var totals = CalculateAndFollow(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: totals, Version: version, Item: item);
    }

    public async Task<CostingResult> RemoveItemAsync(int quoteId, int itemId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);
        if (version.Items.FirstOrDefault(i => i.Id == itemId) is not { } item)
            return ItemNotFound(itemId);
        if (version.Items.Count == 1)
            return new(CostingOutcome.NotApplicable, Problem: "A quote keeps at least one item. Rename this one instead.");

        // The quotation lines copied from the item's materials go first, then the item
        // and its lines are deleted before they leave the version, so nothing is left
        // pointing at a row that is gone.
        var lines = version.LinesOf(item).ToHashSet();
        foreach (var copied in version.QuotationLines.Where(l => l.CostingLine is { } source && lines.Contains(source)).ToList())
        {
            version.RemoveQuotationLine(copied);
            _quotes.RemoveQuotationLine(copied);
        }
        foreach (var line in lines)
            _quotes.RemoveCostingLine(line);
        _quotes.RemoveItem(item);
        version.RemoveItem(item);

        var totals = CalculateAndFollow(version);

        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: totals, Version: version);
    }

    /// <summary>The item named, or the first item when none is; null for an item not on the quote.</summary>
    private static QuoteItem? ItemOrFirst(QuoteVersion version, int? itemId) =>
        itemId is { } id ? version.Items.FirstOrDefault(i => i.Id == id) : version.Items[0];

    private static CostingResult ItemNotFound(int? itemId) =>
        new(CostingOutcome.ItemNotFound, Problem: $"This quote has no item {itemId}.");

    private async Task<CostingResult> AddAsync(Quote quote, QuoteVersion version, QuoteItem item, CostingLine line, CancellationToken ct)
    {
        version.AddCostingLine(line, item);
        var totals = CalculateAndFollow(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, line, totals);
    }

    /// <summary>
    /// The new totals, with the quotation lines copied from the costing brought up to
    /// date: a changed price, quantity, discount, markup or transport amount shows on
    /// the customer quotation at once.
    /// </summary>
    private QuoteTotals CalculateAndFollow(QuoteVersion version)
    {
        var totals = _calculator.Calculate(version);
        foreach (var removed in CostingOnQuotation.Follow(version, totals))
            _quotes.RemoveQuotationLine(removed);
        return totals;
    }

    public async Task<SheetSummary?> GetSheetSummaryAsync(int quoteId, int? versionNo = null, CancellationToken ct = default)
    {
        var quote = versionNo is null ? await _quotes.GetAsync(quoteId, ct) : await _quotes.GetWithAllVersionsAsync(quoteId, ct);
        var version = versionNo is int no ? quote?.Version(no) : quote?.CurrentVersion;
        if (version is null)
            return null;
        await _quotes.LoadBufferStockAsync(version, ct);
        return SheetsAndAdhesive.Summarise(version);
    }

    public async Task<CostingResult> SetBufferStockAsync(int quoteId, string material, decimal sheets, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

        // Buffer stock is for a material the job uses, as the summary lists it.
        if (!version.CostingLines.Any(l => l.LineType == CostingLineType.Material && l.Description == material.Trim()))
            return new(CostingOutcome.NotApplicable, Problem: "This quote has no such material. Add it to an item first.");

        await _quotes.LoadBufferStockAsync(version, ct);
        if (version.SetBufferStock(material, sheets) is { } removed)
            _quotes.RemoveBufferStock(removed);

        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Version: version);
    }

    private static CostingResult Sealed(QuoteVersion version) =>
        new(CostingOutcome.VersionSealed,
            Problem: $"Version {version.VersionNo} is sealed. Reopen the quote to change it.");
}
