using System.ComponentModel.DataAnnotations;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfacesApp.Api;

// The request and response shapes of the costing sheet API. Nothing here is an EF
// entity, so the database model never leaks into a response.
//
// Every type in this file carries cost prices or markup. It is internal to Techno
// Surfaces and must never be used for the customer quotation.

/// <summary>
/// The largest figures the database columns hold: quantities are decimal(18,4) and
/// money is decimal(18,2). A larger value is refused as invalid input rather than
/// failing when it is saved.
/// </summary>
internal static class ColumnLimits
{
    public const decimal Quantity = 99_999_999_999_999.9999m;
    public const decimal Money = 9_999_999_999_999_999.99m;
}

/// <summary>
/// The body of POST /api/quotes/{quoteId}/lines. "material" needs colourId and
/// sheetSizeId; "rate" needs rateItemId. unitPrice is a price typed for this job,
/// accepted only on a rate line whose item has no rate-card price.
/// </summary>
public sealed class AddLineRequest : IValidatableObject
{
    public const string Material = "material";
    public const string Rate = "rate";

    [Required]
    public string Type { get; set; } = "";

    public int? ColourId { get; set; }
    public int? SheetSizeId { get; set; }
    public int? RateItemId { get; set; }

    public decimal Quantity { get; set; }

    public decimal SupplierDiscountPercent { get; set; }

    public decimal? UnitPrice { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Type is not (Material or Rate))
            yield return new($"Type must be \"{Material}\" or \"{Rate}\".", new[] { nameof(Type) });

        if (Type == Material && (ColourId is null || SheetSizeId is null))
            yield return new("A material line needs a colour and a sheet size.", new[] { nameof(ColourId), nameof(SheetSizeId) });

        if (Type == Rate && RateItemId is null)
            yield return new("A rate line needs a rate item.", new[] { nameof(RateItemId) });

        if (Quantity < 0)
            yield return new("A quantity cannot be negative.", new[] { nameof(Quantity) });

        if (Quantity > ColumnLimits.Quantity)
            yield return new("This quantity is too large.", new[] { nameof(Quantity) });

        if (SupplierDiscountPercent is < 0 or > 100)
            yield return new("A discount must be between 0 and 100 per cent.", new[] { nameof(SupplierDiscountPercent) });

        if (Type == Rate && SupplierDiscountPercent != 0)
            yield return new("A supplier discount applies to a material line only.", new[] { nameof(SupplierDiscountPercent) });

        // A material price always comes from the supplier's price list.
        if (Type == Material && UnitPrice is not null)
            yield return new("A material line is priced from the catalogue; a price cannot be typed for it.", new[] { nameof(UnitPrice) });

        // A typed price is a real figure for the job. Zero is exactly the silent
        // failure the system exists to prevent, so it is refused.
        if (UnitPrice <= 0)
            yield return new("A price entered on the quote must be greater than zero.", new[] { nameof(UnitPrice) });

        if (UnitPrice > ColumnLimits.Money)
            yield return new("This price is too large.", new[] { nameof(UnitPrice) });
    }
}

/// <summary>
/// The body of PUT /api/quotes/{quoteId}/lines/{lineId}. Send only what changes.
/// unitPrice charges the line at a typed rate on this quote only; clearPriceOverride
/// goes back to the catalogue price. restoreDerivedQuantity returns a calculated line,
/// such as silicon, to the quantity the calculator works out.
/// </summary>
public sealed class ChangeLineRequest : IValidatableObject
{
    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }
    public bool ClearPriceOverride { get; set; }
    public decimal? SupplierDiscountPercent { get; set; }
    public bool RestoreDerivedQuantity { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Quantity is null && UnitPrice is null && !ClearPriceOverride
            && SupplierDiscountPercent is null && !RestoreDerivedQuantity)
            yield return new("Nothing to change. Send a quantity, unit price, discount or one of the reset flags.");

        if (Quantity < 0)
            yield return new("A quantity cannot be negative.", new[] { nameof(Quantity) });

        // A line is never priced at zero, override included.
        if (UnitPrice <= 0)
            yield return new("A rate override must be greater than zero.", new[] { nameof(UnitPrice) });

        if (Quantity > ColumnLimits.Quantity)
            yield return new("This quantity is too large.", new[] { nameof(Quantity) });

        if (UnitPrice > ColumnLimits.Money)
            yield return new("This price is too large.", new[] { nameof(UnitPrice) });

        if (SupplierDiscountPercent is < 0 or > 100)
            yield return new("A discount must be between 0 and 100 per cent.", new[] { nameof(SupplierDiscountPercent) });

        if (UnitPrice is not null && ClearPriceOverride)
            yield return new("Send a unit price or clear the override, not both.", new[] { nameof(UnitPrice), nameof(ClearPriceOverride) });

        if (Quantity is not null && RestoreDerivedQuantity)
            yield return new("Send a quantity or restore the calculated one, not both.", new[] { nameof(Quantity), nameof(RestoreDerivedQuantity) });
    }
}

/// <summary>The body of PUT /api/quotes/{quoteId}/costing. Send only what changes.</summary>
public sealed class ChangeCostingRequest : IValidatableObject
{
    public decimal? MarkupPercent { get; set; }
    public decimal? TransportAmount { get; set; }

    /// <summary>The travel, inspection and away allowance for the whole job.</summary>
    public decimal? AllowanceAmount { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MarkupPercent is null && TransportAmount is null && AllowanceAmount is null)
            yield return new("Nothing to change. Send a markup percentage, a transport amount or an allowance.");

        // The column holds five digits with two decimals.
        if (MarkupPercent is < 0 or > 999.99m)
            yield return new("A markup must be between 0 and 999.99 per cent.", new[] { nameof(MarkupPercent) });

        if (TransportAmount < 0)
            yield return new("A transport amount cannot be negative.", new[] { nameof(TransportAmount) });

        if (TransportAmount > ColumnLimits.Money)
            yield return new("This transport amount is too large.", new[] { nameof(TransportAmount) });

        if (AllowanceAmount < 0)
            yield return new("An allowance cannot be negative.", new[] { nameof(AllowanceAmount) });

        if (AllowanceAmount > ColumnLimits.Money)
            yield return new("This allowance is too large.", new[] { nameof(AllowanceAmount) });
    }
}

/// <summary>
/// The body of POST /api/quotes/{quoteId}/items and PUT /api/quotes/{quoteId}/items/{itemId}.
/// A new item without a markup takes the quote's.
/// </summary>
public sealed class ItemRequest : IValidatableObject
{
    [Required(ErrorMessage = "Give the item a name, such as Kitchen or Floor unit."), StringLength(QuoteItem.NameMaxLength)]
    public string Name { get; set; } = "";

    [StringLength(QuoteItem.NumberMaxLength)]
    public string? Number { get; set; }

    public decimal Quantity { get; set; } = 1m;

    public decimal? MarkupPercent { get; set; }

    /// <summary>The unit on the bill of quantities. None given is a number of them, No.</summary>
    [StringLength(QuoteItem.UnitMaxLength)]
    public string? Unit { get; set; }

    /// <summary>The heading the item is listed under on the bill of quantities, such as Worktops.</summary>
    [StringLength(QuoteItem.SectionMaxLength)]
    public string? Section { get; set; }

    /// <summary>Prices the item per square metre of its material instead of by its markup. None prices it by its markup.</summary>
    public decimal? PricePerM2 { get; set; }

    public ItemInput ToInput() => new(Name.Trim(), string.IsNullOrWhiteSpace(Number) ? null : Number.Trim(), Quantity, MarkupPercent,
        Unit, Section, PricePerM2);

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Name))
            yield return new("Give the item a name, such as Kitchen or Floor unit.", new[] { nameof(Name) });

        if (Quantity <= 0)
            yield return new("An item is needed at least once.", new[] { nameof(Quantity) });

        if (Quantity > ColumnLimits.Quantity)
            yield return new("This quantity is too large.", new[] { nameof(Quantity) });

        if (MarkupPercent is < 0 or > 999.99m)
            yield return new("A markup must be between 0 and 999.99 per cent.", new[] { nameof(MarkupPercent) });

        if (PricePerM2 < 0)
            yield return new("A price per square metre cannot be negative.", new[] { nameof(PricePerM2) });

        if (PricePerM2 > ColumnLimits.Money)
            yield return new("This price per square metre is too large.", new[] { nameof(PricePerM2) });
    }
}

/// <summary>The body of PUT /api/quotes/{quoteId}/buffer-stock: sheets of one material beyond what the items need.</summary>
public sealed class BufferStockRequest : IValidatableObject
{
    /// <summary>The material as the summary of sheets lists it.</summary>
    [Required, StringLength(QuoteBufferStock.MaterialMaxLength)]
    public string Material { get; set; } = "";

    /// <summary>No sheets takes the buffer off.</summary>
    public decimal Sheets { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Sheets < 0)
            yield return new("Buffer stock cannot be negative.", new[] { nameof(Sheets) });

        if (Sheets > ColumnLimits.Quantity)
            yield return new("This number of sheets is too large.", new[] { nameof(Sheets) });
    }
}

/// <summary>An item on the quote as the costing sheet shows it.</summary>
public sealed record ItemDto(int Id, string? Number, string Name, decimal Quantity, decimal MarkupPercent, string Unit, string? Section,
    decimal? PricePerM2 = null)
{
    public static ItemDto From(QuoteItem item) =>
        new(item.Id, item.Number, item.Name, item.Quantity, item.MarkupPercent, item.Unit, item.Section, item.PricePerM2);
}

public sealed record ItemResponse(ItemDto Item, QuoteTotals Totals)
{
    /// <summary>The item's id at the top level, for a form that opens the item it created.</summary>
    public int Id => Item.Id;
}

/// <summary>A costing line as the costing sheet shows it, with its price and where it came from.</summary>
public sealed record CostingLineDto(
    int Id,
    string LineType,
    string Description,
    decimal ResolvedUnitPrice,
    decimal UnitPrice,
    bool HasPriceOverride,
    string PriceOrigin,
    decimal Quantity,
    bool IsDerived,
    bool IsQuantityOverridden,
    decimal SupplierDiscountPercent,
    decimal LineTotal,
    bool IsBelowTheLine,
    decimal? SheetAreaM2,
    int? RateItemId = null,
    int? ItemId = null)
{
    public static CostingLineDto From(CostingLine line) => new(
        line.Id,
        line.LineType.ToString(),
        line.Description,
        line.ResolvedUnitPrice,
        line.UnitPrice,
        line.HasPriceOverride,
        line.PriceOrigin,
        line.Quantity,
        line.IsDerived,
        line.IsQuantityOverridden,
        line.SupplierDiscountPercent,
        line.LineTotal(),
        line.IsBelowTheLine,
        line.SheetAreaM2,
        line.RateItemId,
        line.Item?.Id ?? line.QuoteItemId);
}

public sealed record LineResponse(CostingLineDto Line, QuoteTotals Totals);

/// <summary>The answer to removing a line: the quote's totals without it.</summary>
public sealed record RemovedLineResponse(QuoteTotals Totals);

/// <summary>
/// The current version of a quote as the costing sheet loads it: every item, and
/// every line with the item it belongs to. <see cref="ForItem"/> narrows the lines
/// to one item, as the costing screen shows them.
/// </summary>
public sealed record CostingSheetDto(
    int QuoteId,
    string Reference,
    string Status,
    int VersionNo,
    bool IsSealed,
    IReadOnlyList<ItemDto> Items,
    IReadOnlyList<CostingLineDto> Lines,
    QuoteTotals Totals)
{
    /// <summary>The same sheet with only one item's lines.</summary>
    public CostingSheetDto ForItem(int itemId) =>
        this with { Lines = Lines.Where(l => l.ItemId == itemId).ToList(), CurrentItemId = itemId };

    /// <summary>The item the lines were narrowed to, if they were.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public int? CurrentItemId { get; init; }

    /// <summary>The figures of the item the lines were narrowed to, if they were.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public ItemTotals? CurrentItem => CurrentItemId is int id ? Totals.Item(id) : null;

    public static CostingSheetDto From(Quote quote, QuoteTotals totals) =>
        From(quote, quote.CurrentVersion!, totals);

    /// <summary>Any version of the quote, such as an earlier one opened from the version history.</summary>
    public static CostingSheetDto From(Quote quote, QuoteVersion version, QuoteTotals totals)
    {
        return new(
            quote.Id,
            quote.Reference,
            quote.Status.ToString(),
            version.VersionNo,
            version.IsSealed,
            version.Items.Select(ItemDto.From).ToList(),
            version.CostingLines.OrderBy(l => l.SortOrder).ThenBy(l => l.Id)
                .Select(l => CostingLineDto.From(l) with { ItemId = version.ItemOf(l).Id }).ToList(),
            totals);
    }
}
