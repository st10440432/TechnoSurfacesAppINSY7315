namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// The rule shared by material prices and rates: for one price key, exactly one
/// price is in force on any day. The spreadsheet allowed two figures for the same
/// thing at once, and which one a lookup found decided the quote.
/// </summary>
internal static class PricePeriod
{
    public static void EnsureCanSupersede(DateOnly effectiveFrom, DateOnly? effectiveTo, DateOnly newFrom)
    {
        if (effectiveTo is not null)
            throw new InvalidOperationException(
                $"This price already ended on {effectiveTo:yyyy-MM-dd}. Only the price currently in force can be replaced.");

        if (newFrom <= effectiveFrom)
            throw new ArgumentOutOfRangeException(nameof(newFrom),
                $"A replacement must start after {effectiveFrom:yyyy-MM-dd}, the day the current price started.");
    }
}
