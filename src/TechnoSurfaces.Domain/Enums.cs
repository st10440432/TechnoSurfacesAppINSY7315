namespace TechnoSurfaces.Domain;

/// <summary>
/// Two roles only. The Managing Director self-approves; an estimator does not.
/// </summary>
public enum UserRole
{
    ManagingDirector,
    Estimator
}

/// <summary>
/// Quote lifecycle. There is deliberately no Rejected state: the client confirmed
/// that the Managing Director corrects an estimator's quote and approves it rather
/// than sending it back.
/// </summary>
public enum QuoteStatus
{
    Draft,
    PendingApproval,
    Approved,
    Sent,
    Accepted,
    Expired
}

/// <summary>
/// The unit a rate item is charged in.
/// </summary>
public enum ChargeUnit
{
    Hour,
    Each,
    Sheet,
    SquareMetre,
    Amount
}

/// <summary>
/// How a costing line's quantity is arrived at. Derived quantities reproduce
/// behaviour already present in the client's spreadsheet.
/// </summary>
public enum DerivationRule
{
    Entered,
    FromTotalAreaM2,
    FromSheetCount
}

/// <summary>
/// Grouping of rate items on the costing sheet. Extras holds the cut-out and
/// groove charges that sit below the markup line.
///
/// Consumables was added after Task 1 because the client's costing sheet groups
/// seamkit, sandpaper, silicon and Genkem together. It goes last so the stored
/// values of the existing members do not move.
/// </summary>
public enum RateCategory
{
    Fabrication,
    Installation,
    Wood,
    SinksAndHardware,
    Extras,
    Consumables
}

/// <summary>
/// Catalogue lifecycle. Entries are retired, never deleted, so that quotes created
/// before a change still resolve. Max on Top flag phased-out items on their list.
/// </summary>
public enum CatalogueStatus
{
    Active,
    PhasingOut,
    Discontinued
}

/// <summary>
/// A costing line is either a material line or a rate line, never both.
/// </summary>
public enum CostingLineType
{
    Material,
    Rate
}

/// <summary>
/// How a supplier publishes prices. Three of the five price by colour band and two
/// price each item individually; neither is the general case.
/// </summary>
public enum PricingStructure
{
    Band,
    Item
}

/// <summary>
/// The sections of standing wording printed on every customer quotation, in the
/// order the client's quotation template prints them. Added last so the stored
/// values of the existing enumerations are unaffected.
/// </summary>
public enum TermSection
{
    Notes,
    LeadTimes,
    Exclusions,
    TermsAndConditions,
    Disclaimers,
    Warranties,
    PaymentTerms,

    /// <summary>
    /// Not seeded: the repository is public and the client's banking details fall
    /// under the non-disclosure agreement. Entered by the Managing Director.
    /// </summary>
    BankDetails
}
