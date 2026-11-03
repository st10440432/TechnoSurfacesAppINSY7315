namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// Sheets of one material ordered beyond what the items need, as the client adds
/// buffer stock to a tender's order on its summary of sheets and adhesive. Typed by
/// the estimator; ordered and paid for, but not charged on any item.
/// </summary>
public class QuoteBufferStock
{
    private QuoteBufferStock() { }

    internal QuoteBufferStock(string material, decimal sheets)
    {
        if (string.IsNullOrWhiteSpace(material))
            throw new ArgumentException("Buffer stock is for a material.", nameof(material));
        Material = material.Trim();
        SetSheets(sheets);
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    /// <summary>The material as the costing sheet describes it: product line, colour and sheet size.</summary>
    public string Material { get; private set; } = "";

    public decimal Sheets { get; private set; }

    public const int MaterialMaxLength = 400;

    internal void SetSheets(decimal sheets)
    {
        if (sheets < 0)
            throw new ArgumentOutOfRangeException(nameof(sheets), "Buffer stock cannot be negative.");
        Sheets = sheets;
    }

    internal QuoteBufferStock CopyForRevision() => new(Material, Sheets);
}
