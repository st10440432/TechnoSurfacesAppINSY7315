using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// Loads and saves quotes for the quoting services. Declared here and implemented in
/// the Infrastructure layer, the same arrangement as ICatalogueReader, so the
/// Application layer does not depend on EF Core.
/// </summary>
public interface IQuoteRepository
{
    /// <summary>
    /// A quote with its customer and contact, and its current version with its
    /// lines and recorded terms, tracked for changes. Enough to read, edit,
    /// approve or reopen the quote. Earlier versions are not loaded, so
    /// <see cref="Quote.Version"/> finds only the current one and
    /// <see cref="Quote.OriginalVersion"/> is not the original: use
    /// <see cref="GetWithAllVersionsAsync"/> to read an earlier version.
    /// </summary>
    Task<Quote?> GetAsync(int quoteId, CancellationToken ct = default);

    /// <summary>As <see cref="GetAsync"/>, with every version, for the version history.</summary>
    Task<Quote?> GetWithAllVersionsAsync(int quoteId, CancellationToken ct = default);

    Task<bool> ReferenceExistsAsync(string reference, CancellationToken ct = default);

    void Add(Quote quote);

    /// <summary>
    /// Deletes a costing line. Taking a line off the version is not enough on its
    /// own: the catalogue and quoting relationships are NoAction, so the row has to
    /// be removed explicitly rather than left as an orphan.
    /// </summary>
    void RemoveCostingLine(CostingLine line);

    /// <summary>Deletes a quotation line, for the same reason as a costing line.</summary>
    void RemoveQuotationLine(QuotationLine line);

    Task SaveChangesAsync(CancellationToken ct = default);
}
