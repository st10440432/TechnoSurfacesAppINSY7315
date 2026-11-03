using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// EF Core implementation of <see cref="IQuoteRepository"/>.
///
/// Scoped to one request, like the DbContext it shares. A quote loaded once in a
/// request is kept and handed back on the next call instead of being queried
/// again: the costing sheet asks for the same quote for the permission check, the
/// costing and the quotation check, and loading it costs six queries each time
/// (NFR-05). The quote is tracked by the same context, so it is the one the
/// request changes and saves.
///
/// Editing, approving and reopening touch only the current version, so
/// <see cref="GetAsync"/> loads only that one: a quote reopened several times would
/// otherwise bring every earlier version and its lines back on every change. The
/// history screens ask for <see cref="GetWithAllVersionsAsync"/>; a quote already
/// loaded with its current version only is completed rather than loaded again.
/// </summary>
public sealed class QuoteRepository : IQuoteRepository
{
    private readonly TechnoSurfacesDbContext _db;
    private readonly Dictionary<int, (Quote Quote, bool AllVersions)> _loaded = new();

    public QuoteRepository(TechnoSurfacesDbContext db) => _db = db;

    public Task<Quote?> GetAsync(int quoteId, CancellationToken ct = default) =>
        LoadAsync(quoteId, allVersions: false, ct);

    public Task<Quote?> GetWithAllVersionsAsync(int quoteId, CancellationToken ct = default) =>
        LoadAsync(quoteId, allVersions: true, ct);

    private async Task<Quote?> LoadAsync(int quoteId, bool allVersions, CancellationToken ct)
    {
        if (_loaded.TryGetValue(quoteId, out var known))
        {
            if (allVersions && !known.AllVersions)
            {
                await LoadEarlierVersionsAsync(known.Quote, ct);
                _loaded[quoteId] = (known.Quote, true);
            }
            return known.Quote;
        }

        var query = _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Contact);

        var quote = await (allVersions ? WithEveryVersion(query) : WithCurrentVersion(query))
            .AsSplitQuery()
            .FirstOrDefaultAsync(q => q.Id == quoteId, ct);

        if (quote is not null)
            _loaded[quoteId] = (quote, allVersions);
        return quote;
    }

    private static IQueryable<Quote> WithEveryVersion(IQueryable<Quote> quotes) =>
        quotes
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .Include(q => q.Versions).ThenInclude(v => v.Items)
            .Include(q => q.Versions).ThenInclude(v => v.QuotationLines)
            .Include(q => q.Versions).ThenInclude(v => v.Terms)
            .Include(q => q.Versions).ThenInclude(v => v.Warranties);

    // The same filter on each include, as EF Core requires for one navigation.
    private static IQueryable<Quote> WithCurrentVersion(IQueryable<Quote> quotes) =>
        quotes
            .Include(q => q.Versions.OrderByDescending(v => v.VersionNo).Take(1)).ThenInclude(v => v.CostingLines)
            .Include(q => q.Versions.OrderByDescending(v => v.VersionNo).Take(1)).ThenInclude(v => v.Items)
            .Include(q => q.Versions.OrderByDescending(v => v.VersionNo).Take(1)).ThenInclude(v => v.QuotationLines)
            .Include(q => q.Versions.OrderByDescending(v => v.VersionNo).Take(1)).ThenInclude(v => v.Terms)
            .Include(q => q.Versions.OrderByDescending(v => v.VersionNo).Take(1)).ThenInclude(v => v.Warranties);

    /// <summary>
    /// The versions before the one already loaded, added to the tracked quote. The
    /// current version is left as it is, with any change the request has made to it.
    /// </summary>
    private async Task LoadEarlierVersionsAsync(Quote quote, CancellationToken ct)
    {
        if (quote.CurrentVersion is not { } current)
            return;

        await _db.Entry(quote).Collection(q => q.Versions).Query()
            .Where(v => v.VersionNo < current.VersionNo)
            .Include(v => v.CostingLines)
            .Include(v => v.Items)
            .Include(v => v.QuotationLines)
            .Include(v => v.Terms)
            .Include(v => v.Warranties)
            .AsSplitQuery()
            .LoadAsync(ct);
    }

    public Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default) =>
        _db.Quotes.AnyAsync(q => q.Reference == reference, ct);

    public void Add(Quote quote) => _db.Quotes.Add(quote);

    public void RemoveCostingLine(CostingLine line) => _db.CostingLines.Remove(line);

    public void RemoveQuotationLine(QuotationLine line) => _db.QuotationLines.Remove(line);

    public void RemoveItem(QuoteItem item) => _db.QuoteItems.Remove(item);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
