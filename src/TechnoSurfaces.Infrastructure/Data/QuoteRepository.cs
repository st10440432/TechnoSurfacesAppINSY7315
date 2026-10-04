using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>EF Core implementation of <see cref="IQuoteRepository"/>.</summary>
public sealed class QuoteRepository : IQuoteRepository
{
    private readonly TechnoSurfacesDbContext _db;

    public QuoteRepository(TechnoSurfacesDbContext db) => _db = db;

    public Task<Quote?> GetAsync(int quoteId, CancellationToken ct = default) =>
        _db.Quotes
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .Include(q => q.Versions).ThenInclude(v => v.QuotationLines)
            .AsSplitQuery()
            .FirstOrDefaultAsync(q => q.Id == quoteId, ct);

    public Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default) =>
        _db.Quotes.AnyAsync(q => q.Reference == reference, ct);

    public void Add(Quote quote) => _db.Quotes.Add(quote);

    public void RemoveCostingLine(CostingLine line) => _db.CostingLines.Remove(line);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
