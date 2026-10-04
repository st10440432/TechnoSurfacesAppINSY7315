namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// A single line of the customer-facing quotation: descriptive, and organised by
/// room or element rather than by material and labour.
///
/// Deliberately distinct from <see cref="CostingLine"/>, because the customer sees
/// what was made and never what it cost. Several costing lines commonly become one
/// quotation line. There is no cost price, supplier discount or markup on this
/// class, so a customer-facing document built from it cannot leak one.
/// </summary>
public class QuotationLine
{
    private QuotationLine() { }

    public QuotationLine(string description, decimal amountExVat, string? room = null, decimal quantity = 1m)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A quotation line needs a description.", nameof(description));
        if (amountExVat < 0)
            throw new ArgumentOutOfRangeException(nameof(amountExVat), "An amount cannot be negative.");

        Description = description;
        AmountExVat = amountExVat;
        Room = room;
        Quantity = quantity;
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    /// <summary>The room or element this line refers to, as the customer would describe it.</summary>
    public string? Room { get; private set; }

    public string Description { get; private set; } = "";

    public decimal Quantity { get; private set; }

    /// <summary>What the customer is charged for this line, excluding VAT.</summary>
    public decimal AmountExVat { get; private set; }

    public int SortOrder { get; set; }

    /// <summary>A copy of this line for a new version of the quote.</summary>
    internal QuotationLine CopyForRevision() =>
        new(Description, AmountExVat, Room, Quantity) { SortOrder = SortOrder };
}
