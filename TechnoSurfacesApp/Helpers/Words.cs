using System.Text;

namespace TechnoSurfacesApp.Helpers;

/// <summary>
/// Turns the names the back end uses for its lists (rate categories, units, terms
/// sections) into the words a user reads, so no screen shows a name such as
/// SinksAndHardware.
/// </summary>
public static class Words
{
    /// <summary>SinksAndHardware becomes "Sinks and hardware".</summary>
    public static string Humanise(string? name)
    {
        if (string.IsNullOrEmpty(name))
            return "";

        var text = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]))
            {
                text.Append(' ');
                text.Append(char.ToLowerInvariant(c));
            }
            else
            {
                text.Append(c);
            }
        }
        return text.ToString();
    }

    /// <summary>How a rate is charged, as written after the price.</summary>
    public static string Unit(string unit) => unit switch
    {
        "Hour" => "per hour",
        "Each" => "each",
        "Sheet" => "per sheet",
        "SquareMetre" => "per m²",
        "Amount" => "fixed amount",
        _ => Humanise(unit).ToLowerInvariant()
    };

    /// <summary>The heading of a block of standing terms on the quotation.</summary>
    public static string TermSection(string section) => section switch
    {
        "BankDetails" => "Banking details",
        _ => Humanise(section)
    };

    /// <summary>What a calculated quantity follows, or null for a quantity the estimator types.</summary>
    public static string? Derivation(string derivation) => derivation switch
    {
        "FromTotalAreaM2" => "Calculated from the total area of material",
        "FromSheetCount" => "Calculated from the number of sheets",
        _ => null
    };
}
