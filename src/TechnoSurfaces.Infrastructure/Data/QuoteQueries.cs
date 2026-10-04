using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// EF Core implementation of <see cref="IQuoteQueries"/> and <see cref="ILapsedQuotes"/>.
///
/// A quote's value is worked out from its current version by the calculator rather
/// than stored, so a total can never disagree with its lines. The reads are
/// no-tracking: working out derived quantities for a total changes nothing saved.
/// </summary>
public sealed class QuoteQueries : IQuoteQueries, ILapsedQuotes
{
    private readonly TechnoSurfacesDbContext _db;
    private readonly IQuoteCalculationService _calculator;

    public QuoteQueries(TechnoSurfacesDbContext db, IQuoteCalculationService calculator)
    {
        _db = db;
        _calculator = calculator;
    }

    public async Task<IReadOnlyList<QuoteSummary>> ListAsync(QuoteListFilter filter, DateOnly today, CancellationToken ct = default)
    {
        var query = WithCosting();

        if (Enum.TryParse<QuoteStatus>(filter.Status, ignoreCase: true, out var status))
            query = query.Where(q => q.Status == status);
        if (filter.CustomerId is { } customerId)
            query = query.Where(q => q.CustomerId == customerId);
        if (!string.IsNullOrWhiteSpace(filter.AuthorId))
            query = query.Where(q => q.CreatedByUserId == filter.AuthorId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim();
            query = query.Where(q =>
                q.Reference.Contains(term)
                || (q.Project != null && q.Project.Contains(term))
                || (q.Site != null && q.Site.Contains(term))
                || q.Customer!.Name.Contains(term));
        }

        var quotes = await query
            .OrderByDescending(q => q.IssueDate).ThenByDescending(q => q.Id)
            .ToListAsync(ct);
        return await SummariseAsync(quotes, today, ct);
    }

    public async Task<IReadOnlyList<QuoteSummary>> ApprovalQueueAsync(DateOnly today, CancellationToken ct = default)
    {
        // Filtered and ordered on the (Status, IssueDate) index: oldest first, so the
        // quote that has waited longest is at the top.
        var quotes = await WithCosting()
            .Where(q => q.Status == QuoteStatus.PendingApproval)
            .OrderBy(q => q.IssueDate).ThenBy(q => q.Id)
            .ToListAsync(ct);
        return await SummariseAsync(quotes, today, ct);
    }

    public Task<int> PendingCountAsync(CancellationToken ct = default) =>
        _db.Quotes.CountAsync(q => q.Status == QuoteStatus.PendingApproval, ct);

    public async Task<QuoteDetail?> GetAsync(int quoteId, DateOnly today, CancellationToken ct = default)
    {
        var quote = await WithCosting().FirstOrDefaultAsync(q => q.Id == quoteId, ct);
        if (quote?.CurrentVersion is not { } version)
            return null;

        var names = await NamesAsync(new[] { quote.CreatedByUserId, quote.ApprovedByUserId }, ct);
        var status = quote.Status.ToString();

        return new QuoteDetail(
            quote.Id,
            quote.Reference,
            status,
            quote.CustomerId,
            quote.Customer!.Name,
            quote.Customer.AccountCode,
            quote.ContactId,
            quote.Contact!.FullName,
            quote.Site,
            quote.Project,
            quote.CustomerReference,
            quote.DeliveryAddress,
            quote.IssueDate,
            quote.ValidUntil,
            Validity.DaysRemaining(quote.ValidUntil, today),
            Validity.ExpiresSoon(status, quote.ValidUntil, today),
            quote.CreatedByUserId,
            Name(names, quote.CreatedByUserId),
            quote.ApprovedByUserId is null ? null : Name(names, quote.ApprovedByUserId),
            quote.ApprovedAtUtc,
            version.VersionNo,
            version.IsSealed,
            version.CostingLines.Count,
            _calculator.Calculate(version));
    }

    public async Task<IReadOnlyList<QuoteVersionSummary>?> VersionsAsync(int quoteId, CancellationToken ct = default)
    {
        var quote = await WithCosting().FirstOrDefaultAsync(q => q.Id == quoteId, ct);
        if (quote is null)
            return null;

        var names = await NamesAsync(quote.Versions.Select(v => v.CreatedByUserId), ct);
        return quote.Versions
            .OrderBy(v => v.VersionNo)
            .Select(v =>
            {
                var totals = _calculator.Calculate(v);
                return new QuoteVersionSummary(
                    v.VersionNo, v.CreatedAtUtc, v.CreatedByUserId, Name(names, v.CreatedByUserId),
                    v.IsSealed, v.MarkupPercent, totals.TotalExVat, totals.TotalIncVat);
            })
            .ToList();
    }

    public async Task<IReadOnlyList<Quote>> FindAsync(DateOnly today, CancellationToken ct = default) =>
        await _db.Quotes
            .Where(q => (q.Status == QuoteStatus.Draft || q.Status == QuoteStatus.Sent) && q.ValidUntil < today)
            .ToListAsync(ct);

    private IQueryable<Quote> WithCosting() =>
        _db.Quotes
            .AsNoTracking()
            .Include(q => q.Customer)
            .Include(q => q.Contact)
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .AsSplitQuery();

    private async Task<IReadOnlyList<QuoteSummary>> SummariseAsync(List<Quote> quotes, DateOnly today, CancellationToken ct)
    {
        var names = await NamesAsync(quotes.Select(q => q.CreatedByUserId), ct);
        return quotes
            .Where(q => q.CurrentVersion is not null)
            .Select(q =>
            {
                var version = q.CurrentVersion!;
                var totals = _calculator.Calculate(version);
                var status = q.Status.ToString();
                return new QuoteSummary(
                    q.Id, q.Reference, status, q.Customer!.Name, q.Contact!.FullName, q.Site, q.Project,
                    q.CreatedByUserId, Name(names, q.CreatedByUserId),
                    q.IssueDate, q.ValidUntil,
                    Validity.DaysRemaining(q.ValidUntil, today),
                    Validity.ExpiresSoon(status, q.ValidUntil, today),
                    version.VersionNo, totals.TotalExVat, totals.TotalIncVat);
            })
            .ToList();
    }

    private async Task<Dictionary<string, string>> NamesAsync(IEnumerable<string?> userIds, CancellationToken ct)
    {
        var ids = userIds.Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().ToList();
        return await _db.Users
            .AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
    }

    /// <summary>The person's name, or the id itself for an account with no domain record.</summary>
    private static string Name(Dictionary<string, string> names, string userId) =>
        names.TryGetValue(userId, out var name) ? name : userId;
}
