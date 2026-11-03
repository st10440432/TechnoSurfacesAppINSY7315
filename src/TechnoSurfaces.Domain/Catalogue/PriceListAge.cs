namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// When a supplier's price list counts as old (Task 1 2.4). One rule, read from the
/// date on the supplier's price list, so the dashboard, the catalogue screens and the
/// review screen always flag the same suppliers.
/// </summary>
public static class PriceListAge
{
    /// <summary>A price list older than this is flagged, so nobody quotes from it unknowingly.</summary>
    public const int OldAfterDays = 365;

    /// <summary>Whether a price list dated <paramref name="listDated"/> is more than a year old on <paramref name="today"/>.</summary>
    public static bool IsOld(DateOnly listDated, DateOnly today) => today.DayNumber - listDated.DayNumber > OldAfterDays;
}
