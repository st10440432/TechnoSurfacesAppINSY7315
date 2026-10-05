using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// The customer quotation (US-10 to US-14), and the customer-facing lines it is
/// built from.
///
/// The document is composed from QuotationLine, the quote header, the standing
/// terms and the brand warranties. It never reads a CostingLine's price, discount or
/// origin, and the markup never leaves this class (Task 1 8.1). The costing is read
/// in two places only: to check that the quotation adds up to it, and to write
/// customer lines from it on request, where each material is given its selling
/// price and no cost, discount or markup is copied.
///
/// Who may change the lines is the CanEditQuote policy, checked by the caller.
/// Reading the quotation is open to every signed-in user.
/// </summary>
public interface IQuotationGenerationService
{
    /// <summary>The customer document, or null for an unknown quote.</summary>
    Task<CustomerQuotation?> GenerateAsync(int quoteId, CancellationToken ct = default);

    /// <summary>
    /// The customer document for any version of the quote, as it was issued (US-21),
    /// or null when the quote or the version does not exist.
    /// </summary>
    Task<CustomerQuotation?> GenerateVersionAsync(int quoteId, int versionNo, CancellationToken ct = default);

    /// <summary>Internal: whether the quotation total equals the costing total (US-10).</summary>
    Task<QuotationCheck?> CheckAsync(int quoteId, CancellationToken ct = default);

    Task<QuotationResult> AddLineAsync(int quoteId, QuotationLineInput input, CancellationToken ct = default);

    Task<QuotationResult> ChangeLineAsync(int quoteId, int lineId, QuotationLineInput input, CancellationToken ct = default);

    Task<QuotationResult> RemoveLineAsync(int quoteId, int lineId, CancellationToken ct = default);

    /// <summary>Puts the lines in the given order. Every line id must appear once.</summary>
    Task<QuotationResult> ReorderAsync(int quoteId, IReadOnlyList<int> lineIds, CancellationToken ct = default);

    /// <summary>
    /// Writes the costing out as customer lines: one line per material, at its
    /// selling price, then one line for the labour, consumables, extras and
    /// transport together. The new lines add up to the costing total excluding
    /// VAT. Lines already on the quotation are kept, and the new ones go after them.
    /// </summary>
    Task<QuotationResult> AddLinesFromCostingAsync(int quoteId, CancellationToken ct = default);
}

public sealed class QuotationGenerationService : IQuotationGenerationService
{
    private readonly IQuoteRepository _quotes;
    private readonly IQuotationTermsReader _terms;
    private readonly IQuoteCalculationService _calculator;

    public QuotationGenerationService(IQuoteRepository quotes, IQuotationTermsReader terms, IQuoteCalculationService calculator)
    {
        _quotes = quotes;
        _terms = terms;
        _calculator = calculator;
    }

    public async Task<CustomerQuotation?> GenerateAsync(int quoteId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        return quote?.CurrentVersion is { } version ? await ComposeAsync(quote, version, ct) : null;
    }

    public async Task<CustomerQuotation?> GenerateVersionAsync(int quoteId, int versionNo, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        return quote?.Version(versionNo) is { } version ? await ComposeAsync(quote, version, ct) : null;
    }

    private async Task<CustomerQuotation> ComposeAsync(Quote quote, QuoteVersion version, CancellationToken ct)
    {
        // An issued version prints the wording it was approved with; a version still
        // being worked on prints the current wording (US-12, US-21).
        IReadOnlyList<QuotationTermSection> terms;
        IReadOnlyList<QuotationWarranty> warranties;
        if (version.HasRecordedTerms)
        {
            terms = Sections(version.Terms.OrderBy(t => t.Section).ThenBy(t => t.SortOrder).Select(t => (t.Section, t.Text)));
            warranties = version.Warranties
                .Select(w => new QuotationWarranty(w.Brand, w.MaterialWarranty, w.WorkmanshipWarranty))
                .ToList();
        }
        else
        {
            terms = Sections((await _terms.GetStandingTermsAsync(ct)).Select(t => (t.Section, t.Text)));
            var materialPriceIds = version.CostingLines
                .Where(l => l.MaterialPriceId is not null)
                .Select(l => l.MaterialPriceId!.Value)
                .Distinct()
                .ToList();
            warranties = (await _terms.GetWarrantiesForMaterialPricesAsync(materialPriceIds, ct))
                .Select(w => new QuotationWarranty(w.Brand, w.MaterialWarranty, w.WorkmanshipWarranty))
                .ToList();
        }

        var subtotal = version.QuotationSubtotalExVat();
        var vat = decimal.Round(subtotal * version.VatRate, 2, MidpointRounding.AwayFromZero);
        var isCurrent = version.VersionNo == quote.CurrentVersion?.VersionNo;

        return new CustomerQuotation(
            quote.Id,
            version.VersionNo,
            // An earlier version is read as what it was when it was replaced, not as the
            // quote's status now.
            isCurrent ? quote.Status.ToString() : version.IsIssued ? "Issued" : "Superseded",
            version.HasRecordedTerms,
            Heading(quote, version),
            Lines(version),
            new QuotationTotals(subtotal, version.VatRate, vat, subtotal + vat),
            terms,
            warranties,
            BankDetailsSet: terms.Any(s => s.Section == nameof(TermSection.BankDetails) && s.Lines.Count > 0),
            IsCurrentVersion: isCurrent,
            HeadingAsIssued: version.IsIssued);
    }

    /// <summary>
    /// The heading block. A version that was issued prints the heading recorded when it
    /// was approved, because the quote's own validity date starts again when it is
    /// reopened and its contact or site may since have been corrected (US-21). A
    /// version not issued yet prints the quote's details as they stand.
    /// </summary>
    private static QuotationHeader Heading(Quote quote, QuoteVersion version) =>
        version.IsIssued
            ? new QuotationHeader(
                Attention: version.IssuedAttention ?? "",
                Company: version.IssuedCompany ?? "",
                Tel: version.IssuedTel,
                Email: version.IssuedEmail,
                Reference: quote.Reference,
                Date: quote.IssueDate,
                ValidUntil: version.IssuedValidUntil ?? quote.ValidUntil,
                Site: version.IssuedSite,
                Project: version.IssuedProject,
                YourRef: version.IssuedCustomerReference)
            : new QuotationHeader(
                Attention: quote.Contact?.FullName ?? "",
                Company: quote.Customer?.Name ?? "",
                Tel: quote.Contact?.Phone,
                Email: quote.Contact?.Email,
                Reference: quote.Reference,
                Date: quote.IssueDate,
                ValidUntil: quote.ValidUntil,
                Site: quote.Site,
                Project: quote.Project,
                YourRef: quote.CustomerReference);

    public async Task<QuotationCheck?> CheckAsync(int quoteId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        return quote?.CurrentVersion is { } version ? Check(version) : null;
    }

    public async Task<QuotationResult> AddLineAsync(int quoteId, QuotationLineInput input, CancellationToken ct = default)
    {
        var (version, refused) = await OpenVersionAsync(quoteId, ct);
        if (refused is not null)
            return refused;

        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(QuotationOutcome.Invalid, Errors: errors);

        var line = new QuotationLine(clean.Description, clean.AmountExVat, clean.Room, clean.Quantity)
        {
            SortOrder = version!.QuotationLines.Count == 0 ? 1 : version.QuotationLines.Max(l => l.SortOrder) + 1
        };
        version.AddQuotationLine(line);
        await _quotes.SaveChangesAsync(ct);
        return new(QuotationOutcome.Ok, ToCustomerLine(line), Check(version));
    }

    public async Task<QuotationResult> ChangeLineAsync(int quoteId, int lineId, QuotationLineInput input, CancellationToken ct = default)
    {
        var (version, refused) = await OpenVersionAsync(quoteId, ct);
        if (refused is not null)
            return refused;

        var line = version!.QuotationLines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return new(QuotationOutcome.LineNotFound);

        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(QuotationOutcome.Invalid, Errors: errors);

        version.ChangeQuotationLine(line, clean.Description, clean.AmountExVat, clean.Room, clean.Quantity);
        await _quotes.SaveChangesAsync(ct);
        return new(QuotationOutcome.Ok, ToCustomerLine(line), Check(version));
    }

    public async Task<QuotationResult> RemoveLineAsync(int quoteId, int lineId, CancellationToken ct = default)
    {
        var (version, refused) = await OpenVersionAsync(quoteId, ct);
        if (refused is not null)
            return refused;

        var line = version!.QuotationLines.FirstOrDefault(l => l.Id == lineId);
        if (line is null)
            return new(QuotationOutcome.LineNotFound);

        version.RemoveQuotationLine(line);
        _quotes.RemoveQuotationLine(line);
        await _quotes.SaveChangesAsync(ct);
        return new(QuotationOutcome.Ok, Check: Check(version));
    }

    public async Task<QuotationResult> ReorderAsync(int quoteId, IReadOnlyList<int> lineIds, CancellationToken ct = default)
    {
        var (version, refused) = await OpenVersionAsync(quoteId, ct);
        if (refused is not null)
            return refused;

        var byId = version!.QuotationLines.ToDictionary(l => l.Id);
        if (lineIds.Count != byId.Count || lineIds.Distinct().Count() != lineIds.Count || lineIds.Any(id => !byId.ContainsKey(id)))
            return new(QuotationOutcome.Invalid, Errors: new Dictionary<string, string[]>
            {
                ["LineIds"] = new[] { "Name every quotation line on the quote exactly once." }
            });

        version.ReorderQuotationLines(lineIds.Select(id => byId[id]).ToList());
        await _quotes.SaveChangesAsync(ct);
        return new(QuotationOutcome.Ok, Check: Check(version));
    }

    /// <summary>The description of the one line that carries everything but the materials.</summary>
    public const string OtherCostsDescription = "Labour, consumables and extras";

    // The item printed beside each copied line: the part of the costing sheet the
    // line comes from, so the customer reads which lines are material and which
    // are the work. Either can be changed afterwards like any other line.

    /// <summary>The item on a line copied from a material on the costing sheet.</summary>
    public const string MaterialItem = "Material";

    /// <summary>The item on the line that carries the labour, consumables and extras.</summary>
    public const string OtherCostsItem = "Labour and extras";

    public async Task<QuotationResult> AddLinesFromCostingAsync(int quoteId, CancellationToken ct = default)
    {
        var (version, refused) = await OpenVersionAsync(quoteId, ct);
        if (refused is not null)
            return refused;

        // Calculated first, so the quantities that follow from the materials are
        // current and the total is the one the approval check compares against.
        var totals = _calculator.Calculate(version!);

        var materials = version!.CostingLines
            .Where(l => l.LineType == CostingLineType.Material && l.LineTotal() > 0)
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
            .ToList();
        var hasOtherCosts = version.TransportAmount > 0
            || version.CostingLines.Any(l => l.LineType == CostingLineType.Rate && l.LineTotal() > 0);

        if (materials.Count == 0 && !hasOtherCosts)
            return new(QuotationOutcome.Invalid, Errors: new Dictionary<string, string[]>
            {
                ["Costing"] = new[] { "There is nothing on the costing sheet to copy. Add the materials and labour first." }
            });

        // A material is shown at its selling price: its cost with the quote's markup.
        // The markup is never shown on its own (US-11).
        var factor = 1m + version.MarkupPercent / 100m;
        var amounts = materials.Select(l => decimal.Round(l.LineTotal() * factor, 2, MidpointRounding.AwayFromZero)).ToList();

        // The one remaining line is the difference to the costing total, so the new
        // lines add up to it to the cent: the marked-up labour and consumables, the
        // cut-outs and grooves at cost, and transport. With no such costs, the cents
        // lost to rounding each material go on the last material instead.
        var remainder = totals.TotalExVat - amounts.Sum();
        if (!hasOtherCosts)
            amounts[^1] += remainder;

        var sortOrder = version.QuotationLines.Count == 0 ? 0 : version.QuotationLines.Max(l => l.SortOrder);
        for (var i = 0; i < materials.Count; i++)
            version.AddQuotationLine(new QuotationLine(materials[i].Description, amounts[i], MaterialItem, materials[i].Quantity)
            {
                SortOrder = ++sortOrder
            });

        if (hasOtherCosts && remainder > 0)
            version.AddQuotationLine(new QuotationLine(OtherCostsDescription, remainder, OtherCostsItem) { SortOrder = ++sortOrder });

        await _quotes.SaveChangesAsync(ct);
        return new(QuotationOutcome.Ok, Check: Check(version));
    }

    private async Task<(QuoteVersion? Version, QuotationResult? Refused)> OpenVersionAsync(int quoteId, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return (null, new(QuotationOutcome.QuoteNotFound));
        if (version.IsSealed)
            return (null, new(QuotationOutcome.VersionSealed,
                Problem: $"Version {version.VersionNo} is sealed. Reopen the quote to change it."));
        return (version, null);
    }

    private QuotationCheck Check(QuoteVersion version)
    {
        var quotation = version.QuotationSubtotalExVat();
        var costing = _calculator.Calculate(version).TotalExVat;
        return new QuotationCheck(version.QuotationLines.Count > 0, quotation, costing, quotation - costing,
            version.QuotationLines.Count > 0 && quotation == costing);
    }

    private static IReadOnlyList<CustomerQuotationLine> Lines(QuoteVersion version) =>
        version.QuotationLines
            .OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
            .Select(ToCustomerLine)
            .ToList();

    private static CustomerQuotationLine ToCustomerLine(QuotationLine l) =>
        new(l.Id, l.Room, l.Description, l.Quantity, l.AmountExVat);

    private static IReadOnlyList<QuotationTermSection> Sections(IEnumerable<(TermSection Section, string Text)> terms) =>
        terms
            .GroupBy(t => t.Section)
            .OrderBy(g => g.Key)
            .Select(g => new QuotationTermSection(g.Key.ToString(), g.Select(t => t.Text).ToList()))
            .ToList();

    // ---- Input rules. Lengths match the QuotationLine columns. ----

    private static QuotationLineInput Normalise(QuotationLineInput i) =>
        new((i.Description ?? "").Trim(), i.AmountExVat, string.IsNullOrWhiteSpace(i.Room) ? null : i.Room.Trim(), i.Quantity);

    private static Dictionary<string, string[]>? Validate(QuotationLineInput i)
    {
        var errors = new Dictionary<string, string[]>();
        if (i.Description.Length == 0)
            errors[nameof(QuotationLineInput.Description)] = new[] { "A quotation line needs a description." };
        else if (i.Description.Length > DescriptionMaxLength)
            errors[nameof(QuotationLineInput.Description)] = new[] { $"No more than {DescriptionMaxLength} characters." };
        if (i.Room is { Length: > RoomMaxLength })
            errors[nameof(QuotationLineInput.Room)] = new[] { $"No more than {RoomMaxLength} characters." };
        if (i.AmountExVat < 0)
            errors[nameof(QuotationLineInput.AmountExVat)] = new[] { "An amount cannot be negative." };
        if (i.Quantity < 0)
            errors[nameof(QuotationLineInput.Quantity)] = new[] { "A quantity cannot be negative." };
        return errors.Count == 0 ? null : errors;
    }

    public const int DescriptionMaxLength = 400;
    public const int RoomMaxLength = 120;
}
