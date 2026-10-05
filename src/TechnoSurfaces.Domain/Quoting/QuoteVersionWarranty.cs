namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// The warranty for one brand on a version, as it stood when the version was
/// approved (US-13). A quote can use material from more than one brand, so a
/// version can carry more than one. Only brands with a confirmed warranty are
/// recorded. Never edited after it is recorded.
/// </summary>
public class QuoteVersionWarranty
{
    private QuoteVersionWarranty() { }

    public QuoteVersionWarranty(string brand, string materialWarranty, string workmanshipWarranty)
    {
        if (string.IsNullOrWhiteSpace(brand))
            throw new ArgumentException("A recorded warranty needs its brand.", nameof(brand));
        if (string.IsNullOrWhiteSpace(materialWarranty) || string.IsNullOrWhiteSpace(workmanshipWarranty))
            throw new ArgumentException("A recorded warranty needs both its material and workmanship periods.");

        Brand = brand;
        MaterialWarranty = materialWarranty;
        WorkmanshipWarranty = workmanshipWarranty;
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    public string Brand { get; private set; } = "";
    public string MaterialWarranty { get; private set; } = "";
    public string WorkmanshipWarranty { get; private set; } = "";
}
