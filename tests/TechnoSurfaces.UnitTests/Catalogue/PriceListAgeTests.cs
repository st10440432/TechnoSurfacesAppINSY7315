using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.UnitTests.Catalogue;

/// <summary>
/// When a supplier's price list counts as old (Task 1 2.4): more than a year after
/// the date on the list. One rule for the dashboard, the catalogue and the review screen.
/// </summary>
public sealed class PriceListAgeTests
{
    private static readonly DateOnly Today = new(2026, 10, 30);

    [Fact]
    public void A_list_exactly_a_year_old_is_not_yet_old() =>
        Assert.False(PriceListAge.IsOld(Today.AddDays(-PriceListAge.OldAfterDays), Today));

    [Fact]
    public void A_list_a_day_over_a_year_old_is_old() =>
        Assert.True(PriceListAge.IsOld(Today.AddDays(-PriceListAge.OldAfterDays - 1), Today));

    [Fact]
    public void A_list_dated_today_is_not_old() =>
        Assert.False(PriceListAge.IsOld(Today, Today));
}
