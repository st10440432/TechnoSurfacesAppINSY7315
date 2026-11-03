namespace TechnoSurfaces.Domain.Quoting;

/// <summary>How the customer quotation lists the work.</summary>
public enum QuotationLayout
{
    /// <summary>The lines the estimator writes, one per room or element, as for a kitchen.</summary>
    Lines = 0,

    /// <summary>
    /// A bill of quantities, as the client sends a tender: one row per item with its
    /// number, description, unit, quantity, rate and amount, under its section. Built
    /// from the items on the costing sheet, so it always adds up to the costing.
    /// </summary>
    BillOfQuantities = 1
}
