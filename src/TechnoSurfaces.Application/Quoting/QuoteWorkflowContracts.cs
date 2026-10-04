using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

// The shapes the quote screens and the /api endpoints work with. None of them is an
// EF entity. They carry cost and markup figures, so they are for internal screens
// only and never for the customer quotation.

/// <summary>
/// A new quote (US-14, US-15). The reference is typed by hand and must be unique.
/// The markup is entered on every quote; there is no default (team decision). The
/// validity period defaults to the 30 days of the client's standing terms and can be
/// set per quote.
/// </summary>
public sealed record NewQuote(
    string Reference,
    int CustomerId,
    int ContactId,
    decimal MarkupPercent,
    string? Site = null,
    string? Project = null,
    string? CustomerReference = null,
    string? DeliveryAddress = null,
    int ValidForDays = Quote.DefaultValidForDays);

/// <summary>The job details printed on the quotation, editable until the quote is approved.</summary>
public sealed record QuoteDetailsInput(string? Site, string? Project, string? CustomerReference, string? DeliveryAddress);

/// <summary>Filters for the quote list. All optional.</summary>
public sealed record QuoteListFilter(string? Status = null, int? CustomerId = null, string? AuthorId = null, string? Search = null);

/// <summary>
/// A row on the quote list, the approval queue (US-17) and the dashboard. The value
/// is worked out from the current version, never stored.
/// </summary>
public sealed record QuoteSummary(
    int Id,
    string Reference,
    string Status,
    string CustomerName,
    string ContactName,
    string? Site,
    string? Project,
    string AuthorId,
    string AuthorName,
    DateOnly IssueDate,
    DateOnly ValidUntil,
    int DaysRemaining,
    bool ExpiresSoon,
    int VersionNo,
    decimal TotalExVat,
    decimal TotalIncVat);

/// <summary>One quote with its current version's totals, for the review and quote screens.</summary>
public sealed record QuoteDetail(
    int Id,
    string Reference,
    string Status,
    int CustomerId,
    string CustomerName,
    string? CustomerAccountCode,
    int ContactId,
    string ContactName,
    string? Site,
    string? Project,
    string? CustomerReference,
    string? DeliveryAddress,
    DateOnly IssueDate,
    DateOnly ValidUntil,
    int DaysRemaining,
    bool ExpiresSoon,
    string AuthorId,
    string AuthorName,
    string? ApprovedByName,
    DateTime? ApprovedAtUtc,
    int VersionNo,
    bool IsSealed,
    int CostingLineCount,
    QuoteTotals Totals);

/// <summary>One version on the version history screen (US-21).</summary>
public sealed record QuoteVersionSummary(
    int VersionNo,
    DateTime CreatedAtUtc,
    string CreatedByUserId,
    string CreatedByName,
    bool IsSealed,
    decimal MarkupPercent,
    decimal TotalExVat,
    decimal TotalIncVat);

public enum WorkflowOutcome
{
    Ok,
    NotFound,

    /// <summary>The input failed validation; <see cref="WorkflowResult.Errors"/> names the fields.</summary>
    Invalid,

    /// <summary>Another quote already has this reference.</summary>
    ReferenceTaken,

    /// <summary>The quote's status does not allow this step. The problem says why.</summary>
    NotAllowed
}

public sealed record WorkflowResult(
    WorkflowOutcome Outcome,
    QuoteDetail? Quote = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Problem = null);

/// <summary>
/// The read side of quoting: lists, the approval queue, one quote and its version
/// history. Implemented in the Infrastructure layer as no-tracking queries.
/// </summary>
public interface IQuoteQueries
{
    Task<IReadOnlyList<QuoteSummary>> ListAsync(QuoteListFilter filter, DateOnly today, CancellationToken ct = default);

    /// <summary>Quotes waiting for approval, oldest first, on the (Status, IssueDate) index.</summary>
    Task<IReadOnlyList<QuoteSummary>> ApprovalQueueAsync(DateOnly today, CancellationToken ct = default);

    Task<int> PendingCountAsync(CancellationToken ct = default);

    Task<QuoteDetail?> GetAsync(int quoteId, DateOnly today, CancellationToken ct = default);

    Task<IReadOnlyList<QuoteVersionSummary>?> VersionsAsync(int quoteId, CancellationToken ct = default);
}

/// <summary>Quotes whose validity has run out, for <see cref="Quote.ExpireIfLapsed"/>.</summary>
public interface ILapsedQuotes
{
    /// <summary>Draft and Sent quotes valid until before <paramref name="today"/>, tracked for changes.</summary>
    Task<IReadOnlyList<Quote>> FindAsync(DateOnly today, CancellationToken ct = default);
}

/// <summary>Today's date in South Africa, where the client works. SAST is UTC+2 all year.</summary>
public static class BusinessDate
{
    public static DateOnly Today(TimeProvider time) =>
        DateOnly.FromDateTime(time.GetUtcNow().UtcDateTime.AddHours(2));
}

/// <summary>
/// The days a quote has left, and whether to warn about it: a Draft or Sent quote in
/// its last seven days (team decision).
/// </summary>
public static class Validity
{
    public const int WarningDays = 7;

    public static int DaysRemaining(DateOnly validUntil, DateOnly today) => validUntil.DayNumber - today.DayNumber;

    public static bool ExpiresSoon(string status, DateOnly validUntil, DateOnly today)
    {
        var days = DaysRemaining(validUntil, today);
        return status is nameof(Domain.QuoteStatus.Draft) or nameof(Domain.QuoteStatus.Sent)
            && days is >= 0 and <= WarningDays;
    }
}
