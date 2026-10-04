namespace TechnoSurfaces.Application.Quoting;

// The customer quotation (US-10 to US-14). Every type from CustomerQuotation down is
// what the customer sees, so none of them has a cost price, supplier discount,
// markup, price origin or costing line on it. A field like that cannot be added by
// accident in a view, because the view has nothing to bind it to. QuotationCheck is
// the one internal type here: it compares the quotation with the costing and is for
// Techno Surfaces' screens only.

/// <summary>The customer-facing quotation document.</summary>
public sealed record CustomerQuotation(
    int QuoteId,
    int VersionNo,
    string Status,

    // True once approved: the wording is then the copy recorded on the version.
    bool IsIssued,
    QuotationHeader Header,
    IReadOnlyList<CustomerQuotationLine> Lines,
    QuotationTotals Totals,
    IReadOnlyList<QuotationTermSection> Terms,
    IReadOnlyList<QuotationWarranty> Warranties,

    // False until the Managing Director enters the bank details. The document must
    // then say they are not set, rather than leave them out silently.
    bool BankDetailsSet);

/// <summary>The heading block, in the order of the client's template.</summary>
public sealed record QuotationHeader(
    string Attention,
    string Company,
    string? Tel,
    string? Email,
    string Reference,
    DateOnly Date,
    DateOnly ValidUntil,
    string? Site,
    string? Project,
    string? YourRef);

/// <summary>A line as the customer reads it: by room or element, with what it costs them.</summary>
public sealed record CustomerQuotationLine(int Id, string? Room, string Description, decimal Quantity, decimal AmountExVat);

public sealed record QuotationTotals(decimal SubtotalExVat, decimal VatRate, decimal Vat, decimal TotalIncVat);

/// <summary>One block of standing wording: notes, lead times, exclusions and so on.</summary>
public sealed record QuotationTermSection(string Section, IReadOnlyList<string> Lines);

public sealed record QuotationWarranty(string Brand, string Material, string Workmanship);

/// <summary>A customer-facing line as typed by the estimator.</summary>
public sealed record QuotationLineInput(string Description, decimal AmountExVat, string? Room = null, decimal Quantity = 1m);

/// <summary>
/// Internal only. Whether the quotation adds up to the costing (US-10), and by how
/// much it is out, so the estimator can fix the lines. The quote cannot be approved
/// until they match.
/// </summary>
public sealed record QuotationCheck(
    bool HasLines,
    decimal QuotationSubtotalExVat,
    decimal CostingTotalExVat,
    decimal Difference,
    bool Matches);

public enum QuotationOutcome
{
    Ok,
    QuoteNotFound,
    LineNotFound,

    /// <summary>The current version is sealed. Reopen the quote to change it.</summary>
    VersionSealed,

    /// <summary>The input failed validation; the errors name the fields.</summary>
    Invalid
}

public sealed record QuotationResult(
    QuotationOutcome Outcome,
    CustomerQuotationLine? Line = null,
    QuotationCheck? Check = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Problem = null);
