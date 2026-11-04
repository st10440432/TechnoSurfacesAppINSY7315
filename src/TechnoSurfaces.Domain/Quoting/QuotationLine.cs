namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// A single line of the customer-facing quotation: descriptive, and organised by
/// room or element rather than by material and labour.
///
/// Deliberately distinct from <see cref="CostingLine"/>, because the customer sees
/// what was made and never what it cost. Several costing lines commonly become one
/// quotation line. There is no cost price, supplier discount or markup on this
/// class, so a customer-facing document built from it cannot leak one.
///
/// A line copied from the costing sheet remembers where it came from: the material
/// line it was copied from, or that it carries the labour, consumables and extras.
/// That is how it follows later changes to the costing and is not copied twice.
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

    /// <summary>The material on the costing sheet this line was copied from, if any.</summary>
    public int? CostingLineId { get; private set; }
    public CostingLine? CostingLine { get; private set; }

    /// <summary>True on the one line that carries the labour, consumables, extras and transport.</summary>
    public bool CarriesOtherCosts { get; private set; }

    /// <summary>A line copied from a material on the costing sheet.</summary>
    public static QuotationLine ForMaterial(CostingLine source, string description, decimal amountExVat, string? room, decimal quantity)
    {
        if (source.LineType != CostingLineType.Material)
            throw new ArgumentException("Only a material line on the costing sheet becomes its own quotation line.", nameof(source));
        return new QuotationLine(description, amountExVat, room, quantity) { CostingLine = source };
    }

    /// <summary>The line that carries everything on the costing sheet but the materials.</summary>
    public static QuotationLine ForOtherCosts(string description, decimal amountExVat, string? room) =>
        new(description, amountExVat, room) { CarriesOtherCosts = true };

    /// <summary>
    /// Ties a line copied before lines remembered their source to the costing it
    /// came from. Made through QuoteVersion, like every change to a line.
    /// </summary>
    internal void Follow(CostingLine? source, bool carriesOtherCosts)
    {
        CostingLine = source;
        CarriesOtherCosts = carriesOtherCosts;
    }

    /// <summary>
    /// Takes a new amount and quantity from the costing. The description and item
    /// stay as written, so a line the estimator has reworded keeps their words.
    /// </summary>
    internal void FollowCosting(decimal amountExVat, decimal quantity)
    {
        if (amountExVat < 0)
            throw new ArgumentOutOfRangeException(nameof(amountExVat), "An amount cannot be negative.");
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A quantity cannot be negative.");

        AmountExVat = amountExVat;
        Quantity = quantity;
    }

    /// <summary>
    /// Rewrites the line. Made through QuoteVersion, which refuses it once the
    /// version is sealed; internal so nothing outside the domain can skip that check.
    /// </summary>
    internal void Change(string description, decimal amountExVat, string? room, decimal quantity)
    {
        if (string.IsNullOrWhiteSpace(description))
            throw new ArgumentException("A quotation line needs a description.", nameof(description));
        if (amountExVat < 0)
            throw new ArgumentOutOfRangeException(nameof(amountExVat), "An amount cannot be negative.");
        if (quantity < 0)
            throw new ArgumentOutOfRangeException(nameof(quantity), "A quantity cannot be negative.");

        Description = description;
        AmountExVat = amountExVat;
        Room = room;
        Quantity = quantity;
    }

    /// <summary>
    /// A copy of this line for a new version of the quote, following the copy of its
    /// costing line on that version.
    /// </summary>
    internal QuotationLine CopyForRevision(CostingLine? copiedSource) =>
        new(Description, AmountExVat, Room, Quantity)
        {
            SortOrder = SortOrder,
            CostingLine = copiedSource,
            CarriesOtherCosts = CarriesOtherCosts
        };
}
