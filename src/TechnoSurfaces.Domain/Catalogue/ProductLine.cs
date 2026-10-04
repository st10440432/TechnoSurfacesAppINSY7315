namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// A material as it is actually sold, combining brand, acrylic type and thickness:
/// Staron 12mm, or Infinito Modified 12mm.
///
/// Thickness belongs at this level because it determines both the price band a
/// colour falls into and the sheet sizes available. Surface Studio list WHITE as
/// group A1 at 6mm but A2 at 12mm, and ASPEN CORAL is A3 under Full Acrylic yet M2
/// under Modified, so the product line genuinely changes the price.
/// </summary>
public class ProductLine
{
    public int Id { get; set; }
    public int SupplierId { get; set; }
    public Supplier? Supplier { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }

    /// <summary>Millimetres.</summary>
    public int ThicknessMm { get; set; }

    /// <summary>
    /// The brand the material is sold under, which decides the warranty printed on
    /// the quotation (US-13). Optional: a product line whose brand has not been
    /// confirmed by the client has none, and its quotation carries no warranty
    /// wording rather than wording guessed from a similar brand.
    /// </summary>
    public int? BrandId { get; set; }
    public Brand? Brand { get; set; }

    public CatalogueStatus Status { get; set; } = CatalogueStatus.Active;

    public ICollection<Colour> Colours { get; set; } = new List<Colour>();
    public ICollection<SheetSize> SheetSizes { get; set; } = new List<SheetSize>();
}
