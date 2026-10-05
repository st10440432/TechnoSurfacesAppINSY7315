using System.Globalization;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>A material line: the colour and sheet size chosen in the cascade.</summary>
public sealed record AddMaterialLine(int ColourId, int SheetSizeId, decimal Quantity, decimal SupplierDiscountPercent);

/// <summary>
/// A line from the rate card. <paramref name="UnitPrice"/> is a price typed for this
/// job, accepted only for an item the rate card has no price for, such as the wood
/// boards, sinks and hardware the client prices per job.
/// </summary>
public sealed record AddRateLine(int RateItemId, decimal Quantity, decimal? UnitPrice = null);

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

/// <summary>The figures held on the version rather than on a line.</summary>
public sealed record ChangeCosting(decimal? MarkupPercent = null, decimal? TransportAmount = null);

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
    NotApplicable
}

public sealed record CostingResult(
    CostingOutcome Outcome,
    Quote? Quote = null,
    CostingLine? Line = null,
    QuoteTotals? Totals = null,
    string? Problem = null,
    QuoteVersion? Version = null);

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
        var quote = await _quotes.GetAsync(quoteId, ct);
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

        return await AddAsync(quote, version, line, ct);
    }

    public async Task<CostingResult> AddRateLineAsync(int quoteId, AddRateLine request, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return new(CostingOutcome.QuoteNotFound);
        if (version.IsSealed)
            return Sealed(version);

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

        return await AddAsync(quote, version, line, ct);
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

        var totals = _calculator.Calculate(version);
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

        version.RemoveCostingLine(line);
        _quotes.RemoveCostingLine(line);

        var totals = _calculator.Calculate(version);
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

        if (request.MarkupPercent is { } markup)
            version.SetMarkupPercent(markup);
        if (request.TransportAmount is { } transport)
            version.SetTransportAmount(transport);

        var totals = _calculator.Calculate(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, Totals: totals);
    }

    private async Task<CostingResult> AddAsync(Quote quote, QuoteVersion version, CostingLine line, CancellationToken ct)
    {
        version.AddCostingLine(line);
        var totals = _calculator.Calculate(version);
        await _quotes.SaveChangesAsync(ct);
        return new(CostingOutcome.Ok, quote, line, totals);
    }

    private static CostingResult Sealed(QuoteVersion version) =>
        new(CostingOutcome.VersionSealed,
            Problem: $"Version {version.VersionNo} is sealed. Reopen the quote to change it.");
}
