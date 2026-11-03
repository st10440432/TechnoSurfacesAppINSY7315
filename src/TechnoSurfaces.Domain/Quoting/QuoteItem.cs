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

    /// <summary>The unit the bill of quantities counts the item in, such as No.</summary>
    public string Unit { get; private set; } = DefaultUnit;

    /// <summary>The heading the item is listed under on a bill of quantities, such as Worktops.</summary>
    public string? Section { get; private set; }

    /// <summary>
    /// A price per square metre of the item's material, when the estimator prices the
    /// item that way instead of by its markup, as the client sometimes prices a job.
    /// Null for an item priced by its markup.
    /// </summary>
    public decimal? PricePerM2 { get; private set; }

    public bool IsPricedByArea => PricePerM2 is not null;

    public int SortOrder { get; set; }

    /// <summary>The name a new quote's first item is given until it is renamed.</summary>
    public const string FirstItemName = "Item 1";

    /// <summary>The unit of every item on the client's tenders: a number of them.</summary>
    public const string DefaultUnit = "No";

    // A bill of quantities describes an item in full, with its sizes and drawing number.
    public const int NameMaxLength = 400;
    public const int NumberMaxLength = 20;
    public const int UnitMaxLength = 20;
    public const int SectionMaxLength = 120;

    // Changed through QuoteVersion, which refuses a change once the version is
    // sealed; internal so nothing outside the domain can skip that check.

    internal void Rename(string name, string? number)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("An item needs a name.", nameof(name));
        Name = name.Trim();
        Number = string.IsNullOrWhiteSpace(number) ? null : number.Trim();
    }

    /// <summary>Where the item sits on a bill of quantities. No unit given is a number of them.</summary>
    internal void PlaceOnBill(string? unit, string? section)
    {
        Unit = string.IsNullOrWhiteSpace(unit) ? DefaultUnit : unit.Trim();
        Section = string.IsNullOrWhiteSpace(section) ? null : section.Trim();
    }

    internal void PriceByArea(decimal? pricePerM2)
    {
        if (pricePerM2 < 0)
            throw new ArgumentOutOfRangeException(nameof(pricePerM2), "A price per square metre cannot be negative.");
        PricePerM2 = pricePerM2;
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
        Unit = Unit,
        Section = Section,
        PricePerM2 = PricePerM2,
        SortOrder = SortOrder
    };
}
