using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Costing;

/// <summary>
/// Computes the totals for a quote version.
///
/// The order of operations was confirmed by the client: markup applies to the
/// sub-total only, and the items falling below that line are principally a recovery
/// of cost. The arithmetic itself lives on <see cref="QuoteVersion"/> and
/// <see cref="CostingLine"/> so that it cannot be bypassed; this service applies the
/// derived quantities first and provides the totals as one result.
///
/// The business case for the system rests on this code. An untested calculation
/// engine would reintroduce the silent pricing errors the system exists to remove.
/// </summary>
public interface IQuoteCalculationService
{
    QuoteTotals Calculate(QuoteVersion version);
}

public sealed class QuoteCalculationService : IQuoteCalculationService
{
    public QuoteTotals Calculate(QuoteVersion version)
    {
        ArgumentNullException.ThrowIfNull(version);

        GuardAgainstUnpricedLines(version);

        // A sealed version is a record of what was issued. Its quantities were
        // fixed when it was sealed and are read, never derived again.
        if (!version.IsSealed)
            ApplyDerivedQuantities(version);

        return new QuoteTotals(
            SubTotalExVat: version.SubTotalExVat(),
            MarkupPercent: version.MarkupPercent,
            MarkupAmount: version.MarkupAmount(),
            BelowTheLineTotal: version.BelowTheLineTotal(),
            TransportAmount: version.TransportAmount,
            TotalExVat: version.TotalExVat(),
            VatRate: version.VatRate,
            VatAmount: version.VatAmount(),
            TotalIncVat: version.TotalIncVat(),
            TotalAreaM2: version.TotalAreaM2(),
            TotalSheetCount: version.TotalSheetCount());
    }

    /// <summary>
    /// A line whose price never resolved must not reach the totals. NFR-01 and
    /// US-03: a missing price is visible rather than appearing as zero, and it
    /// blocks the save.
    /// </summary>
    private static void GuardAgainstUnpricedLines(QuoteVersion version)
    {
        var unpriced = version.CostingLines
            .Where(l => string.IsNullOrWhiteSpace(l.PriceOrigin) || l.PriceOrigin == PriceResolution.UnresolvedOrigin)
            .ToList();

        if (unpriced.Count > 0)
            throw new PriceNotResolvedException(
                "This quote cannot be totalled because the following lines have no resolved price: " +
                string.Join("; ", unpriced.Select(l => l.Description)) +
                ". Resolve or remove them before saving.");
    }

    /// <summary>
    /// Certain quantities follow from the job rather than from the estimator, and
    /// are computed before the totals. This reproduces behaviour already present in
    /// the client's spreadsheet. A derived quantity the estimator has typed over is
    /// left as typed.
    /// </summary>
    private static void ApplyDerivedQuantities(QuoteVersion version)
    {
        var totalArea = version.CostingLines.Sum(l => l.AreaM2());
        var totalSheets = version.CostingLines.Sum(l => l.SheetCount());

        foreach (var line in version.CostingLines)
        {
            switch (line.Derivation)
            {
                case DerivationRule.FromTotalAreaM2:
                    line.SetDerivedQuantity(decimal.Round(totalArea * line.DerivationFactor, 4));
                    break;
                case DerivationRule.FromSheetCount:
                    // Silicon and sealing carries a factor of two: two per sheet.
                    line.SetDerivedQuantity(decimal.Round(totalSheets * line.DerivationFactor, 4));
                    break;
                case DerivationRule.Entered:
                default:
                    break;
            }
        }
    }
}

/// <summary>
/// The figures shown on the costing summary. Internal to Techno Surfaces: this
/// carries cost and markup and must never be projected onto a customer-facing
/// document.
/// </summary>
/// <param name="SubTotalExVat">Sum of line totals above the line.</param>
/// <param name="MarkupAmount">Applied to the sub-total only.</param>
/// <param name="BelowTheLineTotal">Cost recovery items, not marked up. Includes transport.</param>
/// <param name="TransportAmount">The petrol and delivery amount typed for the job.</param>
/// <param name="TotalAreaM2">Total square metres of material, shown on the summary.</param>
public sealed record QuoteTotals(
    decimal SubTotalExVat,
    decimal MarkupPercent,
    decimal MarkupAmount,
    decimal BelowTheLineTotal,
    decimal TransportAmount,
    decimal TotalExVat,
    decimal VatRate,
    decimal VatAmount,
    decimal TotalIncVat,
    decimal TotalAreaM2,
    decimal TotalSheetCount);
