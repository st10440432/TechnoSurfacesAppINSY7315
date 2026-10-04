namespace TechnoSurfaces.Domain.Catalogue;

/// <summary>
/// The brand a material is sold under, as distinct from the supplier who sells it.
/// Salvocorp distributes both Staron and Perago, so the supplier cannot carry the
/// warranty.
///
/// Holds the warranty wording for the brand (US-13). The client's quotation
/// template states it for DuPont Corian and for Avonite and Staron only.
/// For any other brand both periods are left empty until the client confirms them,
/// and the quotation prints no warranty for that brand rather than a guess.
/// </summary>
public class Brand
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    /// <summary>Material warranty as printed on the quotation, for example "10 years".</summary>
    public string? MaterialWarranty { get; set; }

    /// <summary>
    /// Workmanship warranty as printed on the quotation, for example "1 year". Set
    /// together with <see cref="MaterialWarranty"/> or not at all; a check
    /// constraint refuses one without the other.
    /// </summary>
    public string? WorkmanshipWarranty { get; set; }

    public bool HasConfirmedWarranty => MaterialWarranty is not null && WorkmanshipWarranty is not null;

    public ICollection<ProductLine> ProductLines { get; set; } = new List<ProductLine>();
}
