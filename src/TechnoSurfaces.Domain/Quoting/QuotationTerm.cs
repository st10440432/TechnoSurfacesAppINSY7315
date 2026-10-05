namespace TechnoSurfaces.Domain.Quoting;

/// <summary>
/// One line of the standing wording printed on every customer quotation: a lead
/// time, an exclusion, a term or condition, a disclaimer or the payment terms
/// (US-12). Held once and applied to every quotation, so it is maintained in one
/// place instead of being retyped per quote.
///
/// Retired rather than deleted, like the catalogue: a line that no longer applies
/// is made inactive and stops appearing on new quotations.
/// </summary>
public class QuotationTerm
{
    public int Id { get; set; }

    public TermSection Section { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Order within the section, as on the client's template.</summary>
    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;
}
