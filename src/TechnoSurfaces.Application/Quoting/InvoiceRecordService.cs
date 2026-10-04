using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>The Sage Pastel invoice as typed from Pastel.</summary>
public sealed record InvoiceInput(string InvoiceNumber, DateOnly InvoiceDate, decimal AmountIncVat);

/// <summary>
/// The recorded invoice beside what was quoted, so the two systems can be reconciled
/// (US-25). <see cref="Variance"/> is the invoiced amount less the quoted total
/// including VAT; it is not zero when the job was re-measured or changed.
/// </summary>
public sealed record InvoiceRecordView(
    int QuoteId,
    string QuoteReference,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    decimal AmountIncVat,
    decimal QuotedTotalIncVat,
    decimal Variance,
    string RecordedByUserId,
    DateTime RecordedAtUtc);

public enum InvoiceOutcome
{
    Ok,
    QuoteNotFound,

    /// <summary>No invoice has been recorded against this quote yet.</summary>
    NotRecorded,

    /// <summary>The input failed validation; the errors name the fields.</summary>
    Invalid,

    /// <summary>The quote is not accepted, already has an invoice, or the number is in use.</summary>
    Conflict
}

public sealed record InvoiceResult(
    InvoiceOutcome Outcome,
    InvoiceRecordView? Invoice = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Problem = null);

/// <summary>Stores invoice records. Implemented in the Infrastructure layer.</summary>
public interface IInvoiceRecords
{
    Task<InvoiceRecord?> ForQuoteAsync(int quoteId, CancellationToken ct = default);

    Task<bool> NumberInUseAsync(string invoiceNumber, CancellationToken ct = default);

    void Add(InvoiceRecord record);
}

/// <summary>
/// Records the Sage Pastel tax invoice against an accepted quote (US-25). The
/// application records the invoice; it does not produce it. Pastel remains the book
/// of record for invoicing, VAT and sequential numbering.
///
/// Recording is for the Managing Director only (team decision): the caller checks
/// the CanRecordInvoice policy. Reading is open to every signed-in user.
/// </summary>
public interface IInvoiceRecordService
{
    Task<InvoiceResult> GetAsync(int quoteId, CancellationToken ct = default);

    Task<InvoiceResult> RecordAsync(int quoteId, InvoiceInput input, CancellationToken ct = default);
}

public sealed class InvoiceRecordService : IInvoiceRecordService
{
    public const int InvoiceNumberMaxLength = 40;

    private readonly IQuoteRepository _quotes;
    private readonly IInvoiceRecords _invoices;
    private readonly IQuoteCalculationService _calculator;
    private readonly ICurrentUser _user;
    private readonly TimeProvider _time;

    public InvoiceRecordService(
        IQuoteRepository quotes, IInvoiceRecords invoices, IQuoteCalculationService calculator, ICurrentUser user, TimeProvider time)
    {
        _quotes = quotes;
        _invoices = invoices;
        _calculator = calculator;
        _user = user;
        _time = time;
    }

    public async Task<InvoiceResult> GetAsync(int quoteId, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return new(InvoiceOutcome.QuoteNotFound);

        var record = await _invoices.ForQuoteAsync(quoteId, ct);
        return record is null
            ? new(InvoiceOutcome.NotRecorded)
            : new(InvoiceOutcome.Ok, View(quote, record));
    }

    public async Task<InvoiceResult> RecordAsync(int quoteId, InvoiceInput input, CancellationToken ct = default)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return new(InvoiceOutcome.QuoteNotFound);

        var number = (input.InvoiceNumber ?? "").Trim();
        var errors = new Dictionary<string, string[]>();
        if (number.Length == 0)
            errors[nameof(InvoiceInput.InvoiceNumber)] = new[] { "Enter the invoice number from Pastel." };
        else if (number.Length > InvoiceNumberMaxLength)
            errors[nameof(InvoiceInput.InvoiceNumber)] = new[] { $"No more than {InvoiceNumberMaxLength} characters." };
        if (input.AmountIncVat <= 0)
            errors[nameof(InvoiceInput.AmountIncVat)] = new[] { "The invoice amount must be greater than zero." };
        if (input.InvoiceDate > BusinessDate.Today(_time))
            errors[nameof(InvoiceInput.InvoiceDate)] = new[] { "An invoice cannot be dated in the future." };
        else if (input.InvoiceDate < quote.IssueDate)
            errors[nameof(InvoiceInput.InvoiceDate)] = new[] { "An invoice cannot be dated before the quote was issued." };
        if (errors.Count > 0)
            return new(InvoiceOutcome.Invalid, Errors: errors);

        if (quote.Status != QuoteStatus.Accepted)
            return new(InvoiceOutcome.Conflict,
                Problem: $"An invoice is recorded only against an accepted quote. This quote is {quote.Status}.");
        if (await _invoices.ForQuoteAsync(quoteId, ct) is not null)
            return new(InvoiceOutcome.Conflict, Problem: "An invoice is already recorded against this quote.");
        if (await _invoices.NumberInUseAsync(number, ct))
            return new(InvoiceOutcome.Conflict, Problem: $"Invoice {number} is already recorded against another quote.");

        var record = new InvoiceRecord(quoteId, number, input.InvoiceDate, input.AmountIncVat, _user.UserId);
        _invoices.Add(record);
        await _quotes.SaveChangesAsync(ct);
        return new(InvoiceOutcome.Ok, View(quote, record));
    }

    private InvoiceRecordView View(Quote quote, InvoiceRecord record)
    {
        var quoted = _calculator.Calculate(quote.CurrentVersion!).TotalIncVat;
        return new InvoiceRecordView(
            quote.Id, quote.Reference, record.InvoiceNumber, record.InvoiceDate, record.AmountIncVat,
            quoted, record.AmountIncVat - quoted, record.RecordedByUserId, record.RecordedAtUtc);
    }
}
