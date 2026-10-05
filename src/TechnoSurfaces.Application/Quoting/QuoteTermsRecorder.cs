using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// Records on a version the standing wording and the brand warranties it is
/// approved with, so the quotation it was issued with can be reproduced exactly
/// after the wording changes (US-21, US-22).
///
/// Call it immediately before <see cref="Quote.Approve"/>, in the same save. A
/// version still being worked on prints the current wording; only an approved
/// version carries its own copy.
/// </summary>
public interface IQuoteTermsRecorder
{
    Task RecordAsync(QuoteVersion version, CancellationToken ct = default);
}

public sealed class QuoteTermsRecorder : IQuoteTermsRecorder
{
    private readonly IQuotationTermsReader _terms;

    public QuoteTermsRecorder(IQuotationTermsReader terms) => _terms = terms;

    public async Task RecordAsync(QuoteVersion version, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(version);

        var standing = await _terms.GetStandingTermsAsync(ct);

        var materialPriceIds = version.CostingLines
            .Where(l => l.MaterialPriceId is not null)
            .Select(l => l.MaterialPriceId!.Value)
            .Distinct()
            .ToList();
        var warranties = await _terms.GetWarrantiesForMaterialPricesAsync(materialPriceIds, ct);

        // Each section keeps its own order, numbered from one as on the template.
        var terms = standing
            .GroupBy(t => t.Section)
            .SelectMany(g => g.Select((t, i) => new QuoteVersionTerm(t.Section, t.Text, i + 1)));

        version.RecordTerms(
            terms,
            warranties.Select(w => new QuoteVersionWarranty(w.Brand, w.MaterialWarranty, w.WorkmanshipWarranty)));
    }
}
