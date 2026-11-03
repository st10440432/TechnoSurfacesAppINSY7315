namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// One item on a quote: a kitchen, a vanity, or one item of a tender's bill of
/// quantities, such as a floor unit. Each item has its own costing sheet and its own
/// markup, and its sandpaper and silicon follow its own area and sheets, as the
/// client costs a tender with one spreadsheet page per item.
///
/// The costing is for one of the item. <see cref="Quantity"/> is how many the job
/// needs, such as three of the same reception counter, so the item's total is
/// charged that many times.
///
/// A quote starts with one item, so a small job is costed exactly as before.
/// </summary>
public class QuoteItem
{
    private QuoteItem() { }

    public QuoteItem(string name, decimal markupPercent, decimal quantity = 1m, string? number = null)
    {
        Rename(name, number);
        SetQuantity(quantity);
        SetMarkupPercent(markupPercent);
    }

    public int Id { get; private set; }
    public int QuoteVersionId { get; private set; }

    /// <summary>The item's number on the customer's bill of quantities, if it has one.</summary>
    public string? Number { get; private set; }

    /// <summary>What the item is, as the estimator and the customer call it.</summary>
    public string Name { get; private set; } = "";

    /// <summary>How many of this item the job needs. The costing is for one.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>Applied to this item's sub-total only.</summary>
    public decimal MarkupPercent { get; private set; }

    public int SortOrder { get; set; }

    /// <summary>The name a new quote's first item is given until it is renamed.</summary>
    public const string FirstItemName = "Item 1";

    // A bill of quantities describes an item in full, with its sizes and drawing number.
    public const int NameMaxLength = 400;
    public const int NumberMaxLength = 20;

    // Changed through QuoteVersion, which refuses a change once the version is
    // sealed; internal so nothing outside the domain can skip that check.

    internal void Rename(string name, string? number)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An item needs a name.", nameof(name));
        Name = name.Trim();
        Number = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
    }

    internal void SetQuantity(decimal quantity)
    {
        if (quantity <= 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "An item is needed at least once.");
        Quantity = quantity;
    }

    internal void SetMarkupPercent(decimal markupPercent)
    {
        if (markupPercent < 0)
            throw new ArgumentOutOfRangeException(nameof(markupPercent), "A markup cannot be negative.");
        MarkupPercent = markupPercent;
    }

    internal QuoteItem CopyForRevision() => new()
    {
        Number = Number,
        Name = Name,
        Quantity = Quantity,
        MarkupPercent = MarkupPercent,
        SortOrder = SortOrder
    };
}
