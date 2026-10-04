using System.ComponentModel.DataAnnotations;
using TechnoSurfaces.Application.Costing;
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

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (MarkupPercent is null && TransportAmount is null)
            yield return new("Nothing to change. Send a markup percentage or a transport amount.");

        // The column holds five digits with two decimals.
        if (MarkupPercent is < 0 or > 999.99m)
            yield return new("A markup must be between 0 and 999.99 per cent.", new[] { nameof(MarkupPercent) });

        if (TransportAmount < 0)
            yield return new("A transport amount cannot be negative.", new[] { nameof(TransportAmount) });

        if (TransportAmount > ColumnLimits.Money)
            yield return new("This transport amount is too large.", new[] { nameof(TransportAmount) });
    }
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
    decimal? SheetAreaM2)
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
        line.SheetAreaM2);
}

public sealed record LineResponse(CostingLineDto Line, QuoteTotals Totals);

/// <summary>The current version of a quote as the costing sheet loads it.</summary>
public sealed record CostingSheetDto(
    int QuoteId,
    string Reference,
    string Status,
    int VersionNo,
    bool IsSealed,
    IReadOnlyList<CostingLineDto> Lines,
    QuoteTotals Totals)
{
    public static CostingSheetDto From(Quote quote, QuoteTotals totals)
    {
        var version = quote.CurrentVersion!;
        return new(
            quote.Id,
            quote.Reference,
            quote.Status.ToString(),
            version.VersionNo,
            version.IsSealed,
            version.CostingLines.OrderBy(l => l.SortOrder).ThenBy(l => l.Id).Select(CostingLineDto.From).ToList(),
            totals);
    }
}
