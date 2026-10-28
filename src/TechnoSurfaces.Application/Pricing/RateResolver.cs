using System.Globalization;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Application.Pricing;

/// <summary>
/// Resolves the amount charged for a rate item, as at a date.
///
/// Two rules apply, in order. A rate may vary by supplier, because adhesive is R130
/// from two suppliers, R250 from a third and R299 from Max on Top when used on
/// another supplier's material; a supplier-specific amount therefore wins over the
/// general one.
///
/// A rate may also be derived from another rate rather than maintained separately.
/// Overtime is fabrication multiplied by 1.5 and installation mirrors fabrication.
/// Holding those as relationships is the architectural answer to the spreadsheet's
/// core weakness, where a rate card copied into twelve sheets drifted apart and a
/// price change had to be made twelve times.
/// </summary>
public interface IRateResolver
{
    Task<PriceResolution> ResolveAsync(int rateItemId, int? supplierId, DateOnly asAt, CancellationToken ct = default);

    /// <summary>
    /// Every item on the rate card, resolved for all suppliers as at a date, by rate
    /// item id. The same rules as <see cref="ResolveAsync"/>, from one query instead
    /// of two or more per item, for the costing sheet's rate grid.
    /// </summary>
    Task<IReadOnlyDictionary<int, PriceResolution>> ResolveRateCardAsync(DateOnly asAt, CancellationToken ct = default);
}

public sealed class RateResolver : IRateResolver
{
    /// <summary>
    /// Guards against a rate card that has been configured to derive from itself,
    /// directly or in a ring. Without this, a cycle would recurse until the stack
    /// gave out.
    /// </summary>
    private const int MaxDerivationDepth = 5;

    private readonly ICatalogueReader _catalogue;

    public RateResolver(ICatalogueReader catalogue) => _catalogue = catalogue;

    public Task<PriceResolution> ResolveAsync(int rateItemId, int? supplierId, DateOnly asAt, CancellationToken ct = default) =>
        ResolveAsync(new CatalogueSource(_catalogue, supplierId, asAt), rateItemId, asAt, depth: 0, ct);

    public async Task<IReadOnlyDictionary<int, PriceResolution>> ResolveRateCardAsync(DateOnly asAt, CancellationToken ct = default)
    {
        var card = new LoadedCardSource(await _catalogue.GetRateCardAsync(asAt, ct));

        var resolved = new Dictionary<int, PriceResolution>();
        foreach (var id in card.ItemIds)
            resolved[id] = await ResolveAsync(card, id, asAt, depth: 0, ct);
        return resolved;
    }

    private static async Task<PriceResolution> ResolveAsync(
        IRateSource source, int rateItemId, DateOnly asAt, int depth, CancellationToken ct)
    {
        if (depth > MaxDerivationDepth)
            return PriceResolution.Failure(
                $"Rate item {rateItemId} derives from itself. Correct the rate card before quoting.");

        var item = await source.ItemAsync(rateItemId, ct);
        if (item is null)
            return PriceResolution.Failure($"Rate item {rateItemId} is not on the rate card.");

        // A rate of its own takes precedence over a derived one.
        var own = await source.PriceAsync(item, ct);
        if (own is not null)
        {
            var scope = own.SupplierId is null ? "all suppliers" : $"supplier {own.SupplierId}";
            return PriceResolution.Success(
                own.Amount,
                $"Rate card: {item.Name}, {scope}, effective {own.EffectiveFrom:yyyy-MM-dd}");
        }

        if (item.DerivedFromRateItemId is null)
            return PriceResolution.Failure(
                $"No rate is in force on {asAt:yyyy-MM-dd} for {item.Name}. " +
                "The Managing Director sets this on the rate card screen.");

        var multiplier = item.DerivedFromRateItemMultiplier ?? 1m;
        var parent = await ResolveAsync(source, item.DerivedFromRateItemId.Value, asAt, depth + 1, ct);
        var parentName = source.ParentName(item);

        if (!parent.Resolved)
            return PriceResolution.Failure(
                $"{item.Name} is derived from {parentName ?? "another rate"}, " +
                $"which could not be resolved: {parent.FailureReason}");

        var amount = decimal.Round(parent.UnitPrice * multiplier, 2, MidpointRounding.AwayFromZero);
        parentName ??= "the base rate";

        if (multiplier == 1m)
            return PriceResolution.Success(amount, $"Rate card: {item.Name} mirrors {parentName}. {parent.Origin}");

        // Formatted with the invariant culture on purpose. The origin is persisted
        // onto the costing line, so it must not vary with the locale of whichever
        // machine resolved the price: en-ZA would write "1,5" and the App Service
        // default would write "1.5" for the same rate. Trailing zeros are stripped
        // because a decimal loaded from the database keeps its scale.
        var factor = multiplier.ToString("0.##", CultureInfo.InvariantCulture);

        return PriceResolution.Success(
            amount,
            $"Rate card: {item.Name} is {parentName} x {factor}. {parent.Origin}");
    }

    /// <summary>Where the rules read the rate card from.</summary>
    private interface IRateSource
    {
        Task<RateItem?> ItemAsync(int rateItemId, CancellationToken ct);

        /// <summary>The item's own amount in force, or null when it has none.</summary>
        Task<RatePrice?> PriceAsync(RateItem item, CancellationToken ct);

        /// <summary>The name of the item a derived rate comes from, or null when it is not on the card.</summary>
        string? ParentName(RateItem item);
    }

    /// <summary>One item at a time from the catalogue, optionally for a supplier.</summary>
    private sealed class CatalogueSource : IRateSource
    {
        private readonly ICatalogueReader _catalogue;
        private readonly int? _supplierId;
        private readonly DateOnly _asAt;

        public CatalogueSource(ICatalogueReader catalogue, int? supplierId, DateOnly asAt)
        {
            _catalogue = catalogue;
            _supplierId = supplierId;
            _asAt = asAt;
        }

        public Task<RateItem?> ItemAsync(int rateItemId, CancellationToken ct) =>
            _catalogue.GetRateItemAsync(rateItemId, ct);

        public Task<RatePrice?> PriceAsync(RateItem item, CancellationToken ct) =>
            _catalogue.FindRatePriceAsync(item.Id, _supplierId, _asAt, ct);

        public string? ParentName(RateItem item) => item.DerivedFromRateItem?.Name;
    }

    /// <summary>
    /// The whole card, loaded once with each item's general prices in force on the
    /// date. Resolves as <see cref="CatalogueSource"/> does with no supplier: the
    /// newest price in force wins.
    /// </summary>
    private sealed class LoadedCardSource : IRateSource
    {
        private readonly Dictionary<int, RateItem> _items;

        public LoadedCardSource(IEnumerable<RateItem> items) => _items = items.ToDictionary(i => i.Id);

        public IEnumerable<int> ItemIds => _items.Keys;

        public Task<RateItem?> ItemAsync(int rateItemId, CancellationToken ct) =>
            Task.FromResult(_items.GetValueOrDefault(rateItemId));

        public Task<RatePrice?> PriceAsync(RateItem item, CancellationToken ct) =>
            Task.FromResult(item.Prices.OrderByDescending(p => p.EffectiveFrom).FirstOrDefault());

        public string? ParentName(RateItem item) =>
            item.DerivedFromRateItemId is { } id ? _items.GetValueOrDefault(id)?.Name : null;
    }
}
