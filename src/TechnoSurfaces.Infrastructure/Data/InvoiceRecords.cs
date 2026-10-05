using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>EF Core implementation of <see cref="IInvoiceRecords"/>.</summary>
public sealed class InvoiceRecords : IInvoiceRecords
{
    private readonly TechnoSurfacesDbContext _db;

    public InvoiceRecords(TechnoSurfacesDbContext db) => _db = db;

    public Task<InvoiceRecord?> ForQuoteAsync(int quoteId, CancellationToken ct = default) =>
        _db.InvoiceRecords.FirstOrDefaultAsync(i => i.QuoteId == quoteId, ct);

    public Task<bool> NumberInUseAsync(string invoiceNumber, CancellationToken ct = default) =>
        _db.InvoiceRecords.AnyAsync(i => i.InvoiceNumber == invoiceNumber, ct);

    public void Add(InvoiceRecord record) => _db.InvoiceRecords.Add(record);
}
