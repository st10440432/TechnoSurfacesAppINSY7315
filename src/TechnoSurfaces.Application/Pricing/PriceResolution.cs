namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// The outcome of resolving a price.
///
/// The type is shaped so that an unresolved price cannot quietly become zero. There
/// is no implicit conversion to decimal and no nullable price that a caller might
/// coalesce; a caller must read <see cref="Resolved"/> before it can reach a figure.
/// This is NFR-01, the primary requirement, and US-03.
/// </summary>
public sealed record PriceResolution
{
    private readonly decimal _unitPrice;

    private PriceResolution(bool resolved, decimal unitPrice, string origin, string? failureReason, int? sourcePriceId)
    {
        Resolved = resolved;
        _unitPrice = unitPrice;
        Origin = origin;
        FailureReason = failureReason;
        SourcePriceId = sourcePriceId;
    }

    public bool Resolved { get; }

    /// <summary>
    /// Where the price came from, shown alongside it on the costing line. NFR-01
    /// requires the origin to be visible, not just the figure.
    /// </summary>
    public string Origin { get; }

    /// <summary>Why the price could not be resolved. Shown to the estimator.</summary>
    public string? FailureReason { get; }

    /// <summary>
    /// The MaterialPrice row the figure came from, on a resolved material price.
    /// A material costing line records it in CostingLine.MaterialPriceId for
    /// traceability. Null for a rate and for a price that did not resolve.
    /// </summary>
    public int? SourcePriceId { get; }

    /// <summary>
    /// The resolved unit price. Throws when the price did not resolve, so that a
    /// missing price becomes a visible failure rather than a plausible figure.
    /// </summary>
    public decimal UnitPrice => Resolved
        ? _unitPrice
        : throw new PriceNotResolvedException(FailureReason ?? "The price could not be resolved.");

    /// <summary>
    /// The origin recorded against a price that did not resolve. Held as a constant
    /// because the calculation engine rejects a line carrying it, and a magic string
    /// duplicated in two places would let that guard stop working silently.
    /// </summary>
    public const string UnresolvedOrigin = "unresolved";

    public static PriceResolution Success(decimal unitPrice, string origin, int? sourcePriceId = null) =>
        new(true, unitPrice, origin, null, sourcePriceId);

    public static PriceResolution Failure(string reason) =>
        new(false, 0m, UnresolvedOrigin, reason, null);
}

/// <summary>
/// Raised when a price cannot be resolved. Blocks the save rather than allowing a
/// line to be priced at zero, which is the failure the spreadsheet permits and this
/// system exists to remove.
/// </summary>
public sealed class PriceNotResolvedException : Exception
{
    public PriceNotResolvedException(string message) : base(message) { }
}
