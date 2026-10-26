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
/// </summary>
public sealed class QuoteRepository : IQuoteRepository
{
    private readonly TechnoSurfacesDbContext _db;
    private readonly Dictionary<int, Quote> _loaded = new();

    public QuoteRepository(TechnoSurfacesDbContext db) => _db = db;

    public async Task<Quote?> GetAsync(int quoteId, CancellationToken ct = default)
    {
        if (_loaded.TryGetValue(quoteId, out var known))
            return known;

        var quote = await _db.Quotes
            .Include(q => q.Customer)
            .Include(q => q.Contact)
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .Include(q => q.Versions).ThenInclude(v => v.QuotationLines)
            .Include(q => q.Versions).ThenInclude(v => v.Terms)
            .Include(q => q.Versions).ThenInclude(v => v.Warranties)
            .AsSplitQuery()
            .FirstOrDefaultAsync(q => q.Id == quoteId, ct);

        if (quote is not null)
            _loaded[quoteId] = quote;
        return quote;
    }

    public Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default) =>
        _db.Quotes.AnyAsync(q => q.Reference == reference, ct);

    public void Add(Quote quote) => _db.Quotes.Add(quote);

    public void RemoveCostingLine(CostingLine line) => _db.CostingLines.Remove(line);

    public void RemoveQuotationLine(QuotationLine line) => _db.QuotationLines.Remove(line);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
