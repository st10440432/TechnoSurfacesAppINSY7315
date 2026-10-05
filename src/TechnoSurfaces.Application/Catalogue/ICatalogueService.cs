using TechnoSurfaces.Domain;

namespace TechnoSurfaces.Application.Catalogue;


/// <summary>The outcome of a catalogue change, with a message the MD can act on.</summary>
public sealed record CatalogueResult(bool Succeeded, string? Error = null, int? Id = null)
{
    public static CatalogueResult Ok(int? id = null) => new(true, null, id);
    public static CatalogueResult Fail(string error) => new(false, error);
}

// ---- Maintaining the catalogue itself (NFR-10: add, edit and retire, never delete) ----

/// <summary>A supplier on the catalogue's supplier list.</summary>
public sealed record SupplierRow(
    int Id, string Name, string? TradingAs, string PricingStructure, DateOnly PriceListDated,
    int ProductLineCount, int ColourCount);

/// <summary>What the Managing Director types for a supplier.</summary>
public sealed record SupplierInput(
    string Name, string? TradingAs, PricingStructure PricingStructure, DateOnly PriceListDated,
    decimal AdhesivePrice, string? DeliveryTerms);

/// <summary>What the Managing Director types for a colour. A band only for a band-priced supplier.</summary>
public sealed record ColourInput(string Name, string? SupplierCode, string? Range, int? PriceBandId);

public sealed record SheetSizeRow(int Id, int LengthMm, int WidthMm);

public sealed record PriceBandRow(int Id, string Code, string Name);

public sealed record ColourDetail(
    int Id, string Name, string SupplierCode, string? Range, int? PriceBandId, string? Band, CatalogueStatus Status)
{
    public bool IsRetired => Status == CatalogueStatus.Discontinued;
}

public sealed record ProductLineDetail(
    int Id, string Name, int ThicknessMm, int? BrandId, string? Brand, CatalogueStatus Status,
    IReadOnlyList<SheetSizeRow> SheetSizes, IReadOnlyList<PriceBandRow> Bands, IReadOnlyList<ColourDetail> Colours)
{
    public bool IsRetired => Status == CatalogueStatus.Discontinued;
}

/// <summary>One supplier with everything under it, for the supplier screen.</summary>
public sealed record SupplierDetail(
    int Id, string Name, string? TradingAs, PricingStructure PricingStructure, DateOnly PriceListDated,
    decimal AdhesivePrice, string DeliveryTerms, IReadOnlyList<ProductLineDetail> ProductLines)
{
    public bool PricesByBand => PricingStructure == PricingStructure.Band;
}

/// <summary>
/// A costing line that took its price from a given price row: the quote, the version
/// and the price it locked in (Task 1 2.4, US-22). The line keeps that price whatever
/// the catalogue does later.
/// </summary>
public sealed record QuoteUsingPrice(
    int QuoteId, string Reference, int VersionNo, bool IsCurrentVersion, string QuoteStatus,
    DateOnly IssueDate, string Line, decimal Quantity, decimal LockedUnitPrice, decimal? OverriddenUnitPrice,
    DateOnly PriceFrom);

/// <summary>One colour in one sheet size, with the price in force on the date asked for.</summary>
public sealed record CatalogueRow(
    int ColourId, int? PriceBandId, int SheetSizeId,
    string Supplier, string ProductLine, int ThicknessMm,
    string Colour, string SupplierCode, string? Band, string SheetSize,
    bool IsRetired, decimal? PricePerSqm, decimal? PricePerSheet, DateOnly? PriceFrom,
    bool IsPhasingOut = false);

/// <summary>One period in a price's history. Prices are superseded, never overwritten.</summary>
public sealed record PricePeriodRow(
    decimal PricePerSqm, DateOnly From, DateOnly? To, string? CapturedByUserId, DateTime CapturedAtUtc);

/// <summary>One rate on the rate card. Derived rates show their rule instead of a number.</summary>
public sealed record RateCardRow(
    int RateItemId, string Name, string Category, string Unit,
    int? SupplierId, string? Supplier, decimal? Amount, DateOnly? From,
    string? DerivedFrom, decimal? Multiplier, bool IsRetired)
{
    public bool IsDerived => DerivedFrom is not null;

    /// <summary>No price captured yet - the client has not supplied the figure.</summary>
    public bool AwaitingClientFigure => !IsDerived && Amount is null;
}

/// <summary>One line of standing wording on the quotation terms screen.</summary>
public sealed record TermRow(int Id, TermSection Section, string Text, int SortOrder, bool IsActive);

/// <summary>A brand and the warranty printed for it: both periods, or neither.</summary>
public sealed record BrandRow(int Id, string Name, string? MaterialWarranty, string? WorkmanshipWarranty, int ProductLineCount)
{
    public bool HasWarranty => MaterialWarranty is not null && WorkmanshipWarranty is not null;
}

/// <summary>
/// Catalogue and rate-card maintenance for the Managing Director (US-23, US-24, NFR-10).
/// Price rules live in IPriceHistory; this service adds the screens' reads, retirement,
/// and messages the MD can act on. Every change is recorded by the audit interceptor.
/// </summary>
public interface ICatalogueService
{
    Task<IReadOnlyList<CatalogueRow>> GetCatalogueAsync(DateOnly asAt, CancellationToken ct = default);

    Task<IReadOnlyList<PricePeriodRow>> GetPriceHistoryAsync(
        int? colourId, int? priceBandId, int sheetSizeId, CancellationToken ct = default);

    Task<IReadOnlyList<RateCardRow>> GetRateCardAsync(DateOnly asAt, CancellationToken ct = default);

    Task<CatalogueResult> SetMaterialPriceAsync(
        int? colourId, int? priceBandId, int sheetSizeId, decimal pricePerSqm, DateOnly from, CancellationToken ct = default);

    Task<CatalogueResult> SetRateAsync(
        int rateItemId, int? supplierId, decimal amount, DateOnly from, CancellationToken ct = default);

    Task<CatalogueResult> RetireColourAsync(int colourId, CancellationToken ct = default);

    Task<CatalogueResult> RetireProductLineAsync(int productLineId, CancellationToken ct = default);

    /// <summary>Puts a retired colour back on the list a new quote can choose from.</summary>
    Task<CatalogueResult> ReinstateColourAsync(int colourId, CancellationToken ct = default);

    Task<CatalogueResult> ReinstateProductLineAsync(int productLineId, CancellationToken ct = default);

    // Adding to the catalogue (NFR-10). Each returns the new record's id.

    Task<IReadOnlyList<SupplierRow>> GetSuppliersAsync(CancellationToken ct = default);

    Task<SupplierDetail?> GetSupplierAsync(int supplierId, CancellationToken ct = default);

    Task<CatalogueResult> AddSupplierAsync(SupplierInput input, CancellationToken ct = default);

    /// <summary>
    /// Changes a supplier's details, including the date of its price list, which is
    /// what the stale-list warning reads. How it prices cannot change once it has
    /// product lines, because their prices are held one way or the other.
    /// </summary>
    Task<CatalogueResult> UpdateSupplierAsync(int supplierId, SupplierInput input, CancellationToken ct = default);

    Task<CatalogueResult> AddProductLineAsync(int supplierId, string name, int thicknessMm, int? brandId, CancellationToken ct = default);

    Task<CatalogueResult> AddSheetSizeAsync(int productLineId, int lengthMm, int widthMm, CancellationToken ct = default);

    /// <summary>A price band, for a supplier who prices by band only.</summary>
    Task<CatalogueResult> AddPriceBandAsync(int productLineId, string code, string name, CancellationToken ct = default);

    Task<CatalogueResult> AddColourAsync(int productLineId, ColourInput input, CancellationToken ct = default);

    /// <summary>
    /// Corrects a colour's name, code, range or band. Quotes already made keep the
    /// description and price they were created with.
    /// </summary>
    Task<CatalogueResult> UpdateColourAsync(int colourId, ColourInput input, CancellationToken ct = default);

    /// <summary>
    /// Every costing line that took its price from this colour or band at this sheet
    /// size, newest quote first, with the price each one locked in (Task 1 2.4).
    /// </summary>
    Task<IReadOnlyList<QuoteUsingPrice>> GetQuotesUsingPriceAsync(
        int? colourId, int? priceBandId, int sheetSizeId, CancellationToken ct = default);

    // Quotation terms and brand warranties (US-12, US-13).

    Task<IReadOnlyList<TermRow>> GetTermsAsync(CancellationToken ct = default);

    Task<IReadOnlyList<BrandRow>> GetBrandsAsync(CancellationToken ct = default);

    /// <summary>Adds a line at the end of its section. The bank details are entered this way.</summary>
    Task<CatalogueResult> AddTermAsync(TermSection section, string text, CancellationToken ct = default);

    Task<CatalogueResult> UpdateTermAsync(int termId, string text, CancellationToken ct = default);

    /// <summary>Retire, never delete: approved versions keep their own copy of the wording.</summary>
    Task<CatalogueResult> RetireTermAsync(int termId, CancellationToken ct = default);

    /// <summary>Sets both periods, or clears both. Half a warranty is refused.</summary>
    Task<CatalogueResult> SetBrandWarrantyAsync(int brandId, string? materialWarranty, string? workmanshipWarranty, CancellationToken ct = default);
}