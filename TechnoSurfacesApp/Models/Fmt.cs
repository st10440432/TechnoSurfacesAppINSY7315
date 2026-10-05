using System.Globalization;

namespace TechnoSurfacesApp.Helpers;

/// <summary>
/// Formatting helpers. Deliberately culture-independent so the app renders
/// identically on every team member's machine.
/// </summary>
public static class Fmt
{
    private static readonly NumberFormatInfo Za = new()
    {
        NumberDecimalSeparator = ",",
        NumberGroupSeparator = "\u00A0",   // non-breaking space
        NumberGroupSizes = new[] { 3 },
        NumberDecimalDigits = 2
    };

    /// <summary>R6 300,00</summary>
    public static string Rand(decimal value) => "R" + value.ToString("N2", Za);

    /// <summary>6 300,00 - no symbol, for table columns with a currency header.</summary>
    public static string Num(decimal value) => value.ToString("N2", Za);

    /// <summary>Drops the decimals on whole numbers: 6 sheets, 2,5 sheets.</summary>
    public static string Qty(decimal value) =>
        value == Math.Floor(value)
            ? value.ToString("N0", Za)
            : value.ToString("0.##", Za);

    /// <summary>2,7968 m2 - four places, because sheet area matters at that precision.</summary>
    public static string Area(decimal value) => value.ToString("N4", Za) + "\u00A0m\u00B2";

    /// <summary>35% / 12,5%</summary>
    public static string Pct(decimal value) =>
        (value == Math.Floor(value) ? value.ToString("N0", Za) : value.ToString("0.##", Za)) + "%";

    /// <summary>16 Aug 2026</summary>
    public static string Date(DateTime d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>16 Aug 2026, for the domain's calendar dates.</summary>
    public static string Date(DateOnly d) => d.ToString("dd MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>Two decimals for area, which is how the costing sheet shows square metres.</summary>
    public static string Sqm(decimal value) => value.ToString("N2", Za) + " m²";

    /// <summary>
    /// A number for an input's value attribute. Browsers read a number input with a
    /// full stop, so this never follows the regional decimal comma.
    /// </summary>
    public static string Input(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>16 Aug 2026, 14:32</summary>
    public static string DateTimeShort(DateTime d) => d.ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
}