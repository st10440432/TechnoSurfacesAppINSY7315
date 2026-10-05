namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// A named, orderable colour within a product line, carrying the supplier's own
/// product code and a lifecycle status. Several colours commonly share one price
/// band.
///
/// A colour belongs to a product line rather than to a supplier directly: the same
/// colour name under a different line is a different product at a different price.
/// </summary>
public class Colour
{
    public int Id { get; set; }
    public int ProductLineId { get; set; }
    public ProductLine? ProductLine { get; set; }

    /// <summary>Null for suppliers who price each item individually.</summary>
    public int? PriceBandId { get; set; }
    public PriceBand? PriceBand { get; set; }

    public string Name { get; set; } = "";

    /// <summary>
    /// The supplier's own product code. This is what is used when ordering. Empty
    /// where the supplier's list gives none, as on the Staron list.
    /// </summary>
    public string SupplierCode { get; set; } = "";

    /// <summary>
    /// A supplier's named grouping for display, for example Woodcentre's SOLID,
    /// NEBULA, POPLAR, STELLA and MET ranges. Not a price band.
    /// </summary>
    public string? Range { get; set; }

    /// <summary>
    /// Retired, never deleted. A discontinued colour cannot be chosen on a new
    /// quote but must still resolve on quotes that already reference it.
    /// </summary>
    public CatalogueStatus Status { get; set; } = CatalogueStatus.Active;

    public bool IsSelectable => Status != CatalogueStatus.Discontinued;
}
