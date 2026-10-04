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