using System.ComponentModel.DataAnnotations;

namespace TechnoSurfacesApp.Models;

/// <summary>Posted by the price editor. Validated here and again by the price rules.</summary>
public sealed class SetMaterialPriceForm
{
    public int? ColourId { get; set; }
    public int? PriceBandId { get; set; }

    [Range(1, int.MaxValue, ErrorMessage = "Choose a sheet size.")]
    public int SheetSizeId { get; set; }

    [Required(ErrorMessage = "Enter the price per square metre.")]
    [Range(0.01, 1_000_000, ErrorMessage = "Enter a price greater than zero.")]
    public decimal? PricePerSqm { get; set; }

    [Required(ErrorMessage = "Enter the date the price takes effect.")]
    public DateOnly? EffectiveFrom { get; set; }
}

/// <summary>Posted by the rate card.</summary>
public sealed class SetRateForm
{
    [Range(1, int.MaxValue)]
    public int RateItemId { get; set; }

    public int? SupplierId { get; set; }

    [Required(ErrorMessage = "Enter the rate.")]
    [Range(0.01, 1_000_000, ErrorMessage = "Enter a rate greater than zero.")]
    public decimal? Amount { get; set; }

    [Required(ErrorMessage = "Enter the date the rate takes effect.")]
    public DateOnly? EffectiveFrom { get; set; }
}

/// <summary>Posted by the terms screen to add a line to a section.</summary>
public sealed class AddTermForm
{
    public TechnoSurfaces.Domain.TermSection Section { get; set; }

    [Required(ErrorMessage = "Enter the wording.")]
    [StringLength(500, ErrorMessage = "Keep a line to 500 characters.")]
    public string? Text { get; set; }
}

/// <summary>Posted by the terms screen to change a line's wording.</summary>
public sealed class UpdateTermForm
{
    [Range(1, int.MaxValue)]
    public int TermId { get; set; }

    [Required(ErrorMessage = "Enter the wording, or retire the line instead.")]
    [StringLength(500, ErrorMessage = "Keep a line to 500 characters.")]
    public string? Text { get; set; }
}

/// <summary>Posted by the terms screen to set or clear a brand's warranty.</summary>
public sealed class BrandWarrantyForm
{
    [Range(1, int.MaxValue)]
    public int BrandId { get; set; }

    [StringLength(60, ErrorMessage = "Keep each warranty to 60 characters.")]
    public string? MaterialWarranty { get; set; }

    [StringLength(60, ErrorMessage = "Keep each warranty to 60 characters.")]
    public string? WorkmanshipWarranty { get; set; }
}
/// <summary>Posted by the supplier screens to add or change a supplier.</summary>
public sealed class SupplierForm
{
    [Required(ErrorMessage = "Enter the supplier's name.")]
    [StringLength(120, ErrorMessage = "Keep the name to 120 characters.")]
    public string? Name { get; set; }

    [StringLength(120, ErrorMessage = "Keep the trading name to 120 characters.")]
    public string? TradingAs { get; set; }

    [Required(ErrorMessage = "Choose how the supplier prices.")]
    public TechnoSurfaces.Domain.PricingStructure? PricingStructure { get; set; }

    [Required(ErrorMessage = "Enter the date on the supplier's price list.")]
    public DateOnly? PriceListDated { get; set; }

    [Range(0, 1_000_000, ErrorMessage = "Enter an adhesive price of 0 or more.")]
    public decimal AdhesivePrice { get; set; }

    [StringLength(500, ErrorMessage = "Keep the delivery terms to 500 characters.")]
    public string? DeliveryTerms { get; set; }
}

/// <summary>Posted by the supplier screen to add a product line.</summary>
public sealed class ProductLineForm
{
    [Range(1, int.MaxValue)]
    public int SupplierId { get; set; }

    [Required(ErrorMessage = "Enter the product line's name.")]
    [StringLength(120, ErrorMessage = "Keep the name to 120 characters.")]
    public string? Name { get; set; }

    [Required(ErrorMessage = "Enter the thickness in millimetres.")]
    [Range(1, 100, ErrorMessage = "Enter the thickness in millimetres, from 1 to 100.")]
    public int? ThicknessMm { get; set; }

    public int? BrandId { get; set; }
}

/// <summary>Posted by the supplier screen to add a sheet size to a product line.</summary>
public sealed class SheetSizeForm
{
    [Range(1, int.MaxValue)]
    public int ProductLineId { get; set; }

    [Required(ErrorMessage = "Enter the length in millimetres.")]
    [Range(1, 10_000, ErrorMessage = "Enter the length in millimetres, up to 10 000.")]
    public int? LengthMm { get; set; }

    [Required(ErrorMessage = "Enter the width in millimetres.")]
    [Range(1, 10_000, ErrorMessage = "Enter the width in millimetres, up to 10 000.")]
    public int? WidthMm { get; set; }
}

/// <summary>Posted by the supplier screen to add a price band to a band-priced product line.</summary>
public sealed class PriceBandForm
{
    [Range(1, int.MaxValue)]
    public int ProductLineId { get; set; }

    [Required(ErrorMessage = "Enter the band's code.")]
    [StringLength(40, ErrorMessage = "Keep the code to 40 characters.")]
    public string? Code { get; set; }

    [StringLength(120, ErrorMessage = "Keep the name to 120 characters.")]
    public string? Name { get; set; }
}

/// <summary>Posted by the supplier screen to add or correct a colour.</summary>
public sealed class ColourForm
{
    /// <summary>Set when adding a colour.</summary>
    public int ProductLineId { get; set; }

    /// <summary>Set when correcting a colour.</summary>
    public int ColourId { get; set; }

    [Required(ErrorMessage = "Enter the colour's name.")]
    [StringLength(120, ErrorMessage = "Keep the name to 120 characters.")]
    public string? Name { get; set; }

    [StringLength(60, ErrorMessage = "Keep the supplier code to 60 characters.")]
    public string? SupplierCode { get; set; }

    [StringLength(60, ErrorMessage = "Keep the range to 60 characters.")]
    public string? Range { get; set; }

    public int? PriceBandId { get; set; }
}
