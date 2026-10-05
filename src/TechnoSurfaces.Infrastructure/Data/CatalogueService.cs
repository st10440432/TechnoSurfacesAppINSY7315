using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// Managing Director only - enforced by the CanEditCatalogue policy on every
/// action that writes through this service.
/// </summary>
public sealed class CatalogueService : ICatalogueService
{
    private readonly TechnoSurfacesDbContext _db;
    private readonly IPriceHistory _prices;
    private readonly ICurrentUser _currentUser;

    public CatalogueService(TechnoSurfacesDbContext db, IPriceHistory prices, ICurrentUser currentUser)
    {
        _db = db;
        _prices = prices;
        _currentUser = currentUser;
    }

    // ------------------------------------------------------------ reads

    public async Task<IReadOnlyList<CatalogueRow>> GetCatalogueAsync(DateOnly asAt, CancellationToken ct = default)
    {
        var colours = await _db.Colours.AsNoTracking()
            .Include(c => c.ProductLine!).ThenInclude(l => l.Supplier)
            .Include(c => c.ProductLine!).ThenInclude(l => l.SheetSizes)
            .Include(c => c.PriceBand)
            .ToListAsync(ct);

        var inForce = await _db.MaterialPrices.AsNoTracking()
            .Where(p => p.EffectiveFrom <= asAt && (p.EffectiveTo == null || p.EffectiveTo >= asAt))
            .ToListAsync(ct);

        var rows = new List<CatalogueRow>();
        foreach (var colour in colours)
        {
            var line = colour.ProductLine!;
            var byBand = line.Supplier!.PricingStructure == PricingStructure.Band;

            foreach (var size in line.SheetSizes)
            {
                var price = inForce.FirstOrDefault(p => p.SheetSizeId == size.Id &&
                    (byBand ? p.PriceBandId == colour.PriceBandId : p.ColourId == colour.Id));

                rows.Add(new CatalogueRow(
                    colour.Id, byBand ? colour.PriceBandId : null, size.Id,
                    line.Supplier.Name, line.Name, line.ThicknessMm,
                    colour.Name, colour.SupplierCode, colour.PriceBand?.Name, size.ToString(),
                    colour.Status == CatalogueStatus.Discontinued || line.Status == CatalogueStatus.Discontinued,
                    price?.PricePerSqm, price?.PricePerSheet(size), price?.EffectiveFrom,
                    colour.Status == CatalogueStatus.PhasingOut || line.Status == CatalogueStatus.PhasingOut));
            }
        }

        return rows
            .OrderBy(r => r.Supplier).ThenBy(r => r.ProductLine).ThenBy(r => r.Colour).ThenBy(r => r.SheetSize)
            .ToList();
    }

    public async Task<IReadOnlyList<PricePeriodRow>> GetPriceHistoryAsync(
        int? colourId, int? priceBandId, int sheetSizeId, CancellationToken ct = default) =>
        await _db.MaterialPrices.AsNoTracking()
            .Where(p => p.SheetSizeId == sheetSizeId && p.ColourId == colourId && p.PriceBandId == priceBandId)
            .OrderByDescending(p => p.EffectiveFrom)
            .Select(p => new PricePeriodRow(p.PricePerSqm, p.EffectiveFrom, p.EffectiveTo, p.CapturedByUserId, p.CapturedAtUtc))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<RateCardRow>> GetRateCardAsync(DateOnly asAt, CancellationToken ct = default)
    {
        var items = await _db.RateItems.AsNoTracking().Include(r => r.DerivedFromRateItem).ToListAsync(ct);
        var suppliers = await _db.Suppliers.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var inForce = await _db.RatePrices.AsNoTracking()
            .Where(p => p.EffectiveFrom <= asAt && (p.EffectiveTo == null || p.EffectiveTo >= asAt))
            .ToListAsync(ct);

        var rows = new List<RateCardRow>();
        foreach (var item in items)
        {
            var retired = item.Status == CatalogueStatus.Discontinued;
            var derivedFrom = item.DerivedFromRateItem?.Name;
            var prices = inForce.Where(p => p.RateItemId == item.Id).ToList();

            if (prices.Count == 0)
            {
                rows.Add(new RateCardRow(item.Id, item.Name, item.Category.ToString(), item.Unit.ToString(),
                    null, null, null, null, derivedFrom, item.DerivedFromRateItemMultiplier, retired));
                continue;
            }

            foreach (var p in prices)
                rows.Add(new RateCardRow(item.Id, item.Name, item.Category.ToString(), item.Unit.ToString(),
                    p.SupplierId, p.SupplierId is int s ? suppliers.GetValueOrDefault(s) : null,
                    p.Amount, p.EffectiveFrom, derivedFrom, item.DerivedFromRateItemMultiplier, retired));
        }

        return rows.OrderBy(r => r.Category).ThenBy(r => r.Name).ToList();
    }

    // ------------------------------------------------------------ writes

    public async Task<CatalogueResult> SetMaterialPriceAsync(
        int? colourId, int? priceBandId, int sheetSizeId, decimal pricePerSqm, DateOnly from, CancellationToken ct = default)
    {
        if (colourId is int id)
        {
            var colour = await _db.Colours.AsNoTracking().Include(c => c.ProductLine)
                .FirstOrDefaultAsync(c => c.Id == id, ct);
            if (colour is null)
                return CatalogueResult.Fail("That colour is not in the catalogue.");
            if (colour.Status == CatalogueStatus.Discontinued || colour.ProductLine?.Status == CatalogueStatus.Discontinued)
                return CatalogueResult.Fail($"{colour.Name} has been retired and cannot be given a new price.");
        }

        return await AttemptAsync(() => _prices.SetMaterialPriceAsync(
            colourId, priceBandId, sheetSizeId, pricePerSqm, from, _currentUser.UserId, ct));
    }

    public Task<CatalogueResult> SetRateAsync(
        int rateItemId, int? supplierId, decimal amount, DateOnly from, CancellationToken ct = default) =>
        AttemptAsync(() => _prices.SetRateAsync(rateItemId, supplierId, amount, from, ct));

    public async Task<CatalogueResult> RetireColourAsync(int colourId, CancellationToken ct = default)
    {
        var colour = await _db.Colours.FindAsync(new object[] { colourId }, ct);
        if (colour is null)
            return CatalogueResult.Fail("That colour is not in the catalogue.");

        // US-24: retire, never delete. Existing quotes keep their copied price and
        // still show the colour; it can no longer be chosen on a new quote.
        colour.Status = CatalogueStatus.Discontinued;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    public async Task<CatalogueResult> RetireProductLineAsync(int productLineId, CancellationToken ct = default)
    {
        var line = await _db.ProductLines.FindAsync(new object[] { productLineId }, ct);
        if (line is null)
            return CatalogueResult.Fail("That product line is not in the catalogue.");

        line.Status = CatalogueStatus.Discontinued;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    public async Task<CatalogueResult> ReinstateColourAsync(int colourId, CancellationToken ct = default)
    {
        var colour = await _db.Colours.Include(c => c.ProductLine).FirstOrDefaultAsync(c => c.Id == colourId, ct);
        if (colour is null)
            return CatalogueResult.Fail("That colour is not in the catalogue.");
        if (colour.ProductLine?.Status == CatalogueStatus.Discontinued)
            return CatalogueResult.Fail($"{colour.ProductLine.Name} is retired. Reinstate the product line first.");

        colour.Status = CatalogueStatus.Active;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(colour.Id);
    }

    public async Task<CatalogueResult> ReinstateProductLineAsync(int productLineId, CancellationToken ct = default)
    {
        var line = await _db.ProductLines.FindAsync(new object[] { productLineId }, ct);
        if (line is null)
            return CatalogueResult.Fail("That product line is not in the catalogue.");

        line.Status = CatalogueStatus.Active;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(line.Id);
    }

    // ------------------------------------------------------------ adding to the catalogue (NFR-10)

    // Column widths, so a value that would not fit is refused with a message rather
    // than failing at the database.
    private const int MaxNameLength = 120;
    private const int MaxCodeLength = 60;
    private const int MaxBandCodeLength = 40;
    private const int MaxDeliveryTermsLength = 500;

    // Guards against a mistyped figure, such as 36800 for 3680. Not client rules.
    private const int MaxThicknessMm = 100;
    private const int MaxSheetMm = 10000;

    public async Task<IReadOnlyList<SupplierRow>> GetSuppliersAsync(CancellationToken ct = default)
    {
        var rows = await _db.Suppliers.AsNoTracking()
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.TradingAs, s.PricingStructure, s.PriceListDated,
                Lines = s.ProductLines.Count,
                Colours = s.ProductLines.SelectMany(l => l.Colours).Count()
            })
            .ToListAsync(ct);

        return rows
            .Select(r => new SupplierRow(r.Id, r.Name, r.TradingAs, r.PricingStructure.ToString(), r.PriceListDated, r.Lines, r.Colours))
            .ToList();
    }

    public async Task<SupplierDetail?> GetSupplierAsync(int supplierId, CancellationToken ct = default)
    {
        var supplier = await _db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, ct);
        if (supplier is null)
            return null;

        var lines = await _db.ProductLines.AsNoTracking()
            .Where(l => l.SupplierId == supplierId)
            .Include(l => l.Brand)
            .Include(l => l.SheetSizes)
            .Include(l => l.Colours).ThenInclude(c => c.PriceBand)
            .AsSplitQuery()
            .ToListAsync(ct);

        var lineIds = lines.Select(l => l.Id).ToList();
        var bands = await _db.PriceBands.AsNoTracking()
            .Where(b => lineIds.Contains(b.ProductLineId))
            .ToListAsync(ct);

        return new SupplierDetail(
            supplier.Id, supplier.Name, supplier.TradingAs, supplier.PricingStructure, supplier.PriceListDated,
            supplier.AdhesivePrice, supplier.DeliveryTerms,
            lines
                .OrderBy(l => l.Name).ThenBy(l => l.ThicknessMm)
                .Select(l => new ProductLineDetail(
                    l.Id, l.Name, l.ThicknessMm, l.BrandId, l.Brand?.Name, l.Status,
                    l.SheetSizes.OrderBy(z => z.LengthMm).ThenBy(z => z.WidthMm)
                        .Select(z => new SheetSizeRow(z.Id, z.LengthMm, z.WidthMm)).ToList(),
                    bands.Where(b => b.ProductLineId == l.Id).OrderBy(b => b.Code)
                        .Select(b => new PriceBandRow(b.Id, b.Code, b.Name)).ToList(),
                    l.Colours.OrderBy(c => c.Name)
                        .Select(c => new ColourDetail(c.Id, c.Name, c.SupplierCode, c.Range, c.PriceBandId, c.PriceBand?.Code, c.Status))
                        .ToList()))
                .ToList());
    }

    public async Task<CatalogueResult> AddSupplierAsync(SupplierInput input, CancellationToken ct = default)
    {
        if (ValidateSupplier(input) is { } problem)
            return CatalogueResult.Fail(problem);

        var name = input.Name.Trim();
        if (await _db.Suppliers.AnyAsync(s => s.Name == name, ct))
            return CatalogueResult.Fail($"There is already a supplier called {name}.");

        var supplier = new Supplier();
        ApplySupplier(supplier, input);
        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(supplier.Id);
    }

    public async Task<CatalogueResult> UpdateSupplierAsync(int supplierId, SupplierInput input, CancellationToken ct = default)
    {
        if (ValidateSupplier(input) is { } problem)
            return CatalogueResult.Fail(problem);

        var supplier = await _db.Suppliers.FindAsync(new object[] { supplierId }, ct);
        if (supplier is null)
            return CatalogueResult.Fail("That supplier is not in the catalogue.");

        var name = input.Name.Trim();
        if (await _db.Suppliers.AnyAsync(s => s.Name == name && s.Id != supplierId, ct))
            return CatalogueResult.Fail($"There is already a supplier called {name}.");

        // Prices are held on the colour or on the band depending on how the supplier
        // prices, so switching would leave every existing price unreachable.
        if (input.PricingStructure != supplier.PricingStructure
            && await _db.ProductLines.AnyAsync(l => l.SupplierId == supplierId, ct))
            return CatalogueResult.Fail(
                $"{supplier.Name} already has product lines priced by {Describe(supplier.PricingStructure)}, so how it prices cannot change.");

        ApplySupplier(supplier, input);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(supplier.Id);
    }

    public async Task<CatalogueResult> AddProductLineAsync(
        int supplierId, string name, int thicknessMm, int? brandId, CancellationToken ct = default)
    {
        var lineName = Clean(name);
        if (lineName is null)
            return CatalogueResult.Fail("Enter the product line's name.");
        if (lineName.Length > MaxNameLength)
            return CatalogueResult.Fail($"Keep the name to {MaxNameLength} characters.");
        if (thicknessMm is < 1 or > MaxThicknessMm)
            return CatalogueResult.Fail($"Enter the thickness in millimetres, from 1 to {MaxThicknessMm}.");

        if (!await _db.Suppliers.AnyAsync(s => s.Id == supplierId, ct))
            return CatalogueResult.Fail("That supplier is not in the catalogue.");
        if (brandId is int brand && !await _db.Brands.AnyAsync(b => b.Id == brand, ct))
            return CatalogueResult.Fail("That brand is not in the catalogue.");
        if (await _db.ProductLines.AnyAsync(l => l.SupplierId == supplierId && l.Name == lineName && l.ThicknessMm == thicknessMm, ct))
            return CatalogueResult.Fail($"This supplier already has {lineName} at {thicknessMm} mm.");

        var line = new ProductLine { SupplierId = supplierId, Name = lineName, ThicknessMm = thicknessMm, BrandId = brandId };
        _db.ProductLines.Add(line);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(line.Id);
    }

    public async Task<CatalogueResult> AddSheetSizeAsync(int productLineId, int lengthMm, int widthMm, CancellationToken ct = default)
    {
        if (lengthMm is < 1 or > MaxSheetMm || widthMm is < 1 or > MaxSheetMm)
            return CatalogueResult.Fail($"Enter the length and width in millimetres, up to {MaxSheetMm}.");

        var (_, error) = await OpenLineAsync(productLineId, ct);
        if (error is not null)
            return CatalogueResult.Fail(error);
        if (await _db.SheetSizes.AnyAsync(z => z.ProductLineId == productLineId && z.LengthMm == lengthMm && z.WidthMm == widthMm, ct))
            return CatalogueResult.Fail($"This product line already has a {lengthMm} x {widthMm} sheet.");

        var size = new SheetSize { ProductLineId = productLineId, LengthMm = lengthMm, WidthMm = widthMm };
        _db.SheetSizes.Add(size);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(size.Id);
    }

    public async Task<CatalogueResult> AddPriceBandAsync(int productLineId, string code, string name, CancellationToken ct = default)
    {
        var bandCode = Clean(code);
        if (bandCode is null)
            return CatalogueResult.Fail("Enter the band's code as the supplier's list gives it.");
        var bandName = Clean(name) ?? bandCode;
        if (bandCode.Length > MaxBandCodeLength || bandName.Length > MaxNameLength)
            return CatalogueResult.Fail($"Keep the code to {MaxBandCodeLength} characters and the name to {MaxNameLength}.");

        var (line, error) = await OpenLineAsync(productLineId, ct);
        if (error is not null)
            return CatalogueResult.Fail(error);
        if (line!.Supplier!.PricingStructure != PricingStructure.Band)
            return CatalogueResult.Fail($"{line.Supplier.Name} prices each colour on its own, so it has no price bands.");
        if (await _db.PriceBands.AnyAsync(b => b.ProductLineId == productLineId && b.Code == bandCode, ct))
            return CatalogueResult.Fail($"This product line already has band {bandCode}.");

        var band = new PriceBand { SupplierId = line.SupplierId, ProductLineId = productLineId, Code = bandCode, Name = bandName };
        _db.PriceBands.Add(band);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(band.Id);
    }

    public async Task<CatalogueResult> AddColourAsync(int productLineId, ColourInput input, CancellationToken ct = default)
    {
        var (line, error) = await OpenLineAsync(productLineId, ct);
        if (error is not null)
            return CatalogueResult.Fail(error);
        if (await ValidateColourAsync(line!, input, exceptColourId: null, ct) is { } problem)
            return CatalogueResult.Fail(problem);

        var colour = new Colour { ProductLineId = productLineId };
        ApplyColour(colour, input);
        _db.Colours.Add(colour);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(colour.Id);
    }

    public async Task<CatalogueResult> UpdateColourAsync(int colourId, ColourInput input, CancellationToken ct = default)
    {
        var colour = await _db.Colours
            .Include(c => c.ProductLine!).ThenInclude(l => l.Supplier)
            .FirstOrDefaultAsync(c => c.Id == colourId, ct);
        if (colour is null)
            return CatalogueResult.Fail("That colour is not in the catalogue.");
        if (await ValidateColourAsync(colour.ProductLine!, input, colourId, ct) is { } problem)
            return CatalogueResult.Fail(problem);

        ApplyColour(colour, input);
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok(colour.Id);
    }

    public async Task<IReadOnlyList<QuoteUsingPrice>> GetQuotesUsingPriceAsync(
        int? colourId, int? priceBandId, int sheetSizeId, CancellationToken ct = default)
    {
        // Every price row there has been for this colour or band at this size, so a
        // quote made under an earlier price is listed too, with the figure it kept.
        var rows = await (
                from line in _db.CostingLines.AsNoTracking()
                join price in _db.MaterialPrices on line.MaterialPriceId equals price.Id
                join version in _db.QuoteVersions on line.QuoteVersionId equals version.Id
                join quote in _db.Quotes on version.QuoteId equals quote.Id
                where price.SheetSizeId == sheetSizeId
                   && (colourId != null ? price.ColourId == colourId : price.PriceBandId == priceBandId)
                select new
                {
                    quote.Id, quote.Reference, quote.Status, quote.IssueDate, version.VersionNo,
                    Latest = _db.QuoteVersions.Where(v => v.QuoteId == quote.Id).Max(v => v.VersionNo),
                    line.Description, line.Quantity, line.ResolvedUnitPrice, line.OverriddenUnitPrice,
                    price.EffectiveFrom
                })
            .ToListAsync(ct);

        return rows
            .OrderByDescending(r => r.IssueDate).ThenByDescending(r => r.Id).ThenByDescending(r => r.VersionNo)
            .Select(r => new QuoteUsingPrice(
                r.Id, r.Reference, r.VersionNo, r.VersionNo == r.Latest, r.Status.ToString(), r.IssueDate,
                r.Description, r.Quantity, r.ResolvedUnitPrice, r.OverriddenUnitPrice, r.EffectiveFrom))
            .ToList();
    }

    // ---- rules shared by the catalogue additions

    /// <summary>A product line that exists and is not retired, with its supplier.</summary>
    private async Task<(ProductLine? Line, string? Error)> OpenLineAsync(int productLineId, CancellationToken ct)
    {
        var line = await _db.ProductLines.Include(l => l.Supplier).FirstOrDefaultAsync(l => l.Id == productLineId, ct);
        if (line is null)
            return (null, "That product line is not in the catalogue.");
        if (line.Status == CatalogueStatus.Discontinued)
            return (null, $"{line.Name} is retired. Reinstate it before adding to it.");
        return (line, null);
    }

    private async Task<string?> ValidateColourAsync(ProductLine line, ColourInput input, int? exceptColourId, CancellationToken ct)
    {
        var name = Clean(input.Name);
        if (name is null)
            return "Enter the colour's name.";
        if (name.Length > MaxNameLength)
            return $"Keep the name to {MaxNameLength} characters.";
        if (Clean(input.SupplierCode)?.Length > MaxCodeLength || Clean(input.Range)?.Length > MaxCodeLength)
            return $"Keep the supplier code and the range to {MaxCodeLength} characters.";

        if (await _db.Colours.AnyAsync(c => c.ProductLineId == line.Id && c.Name == name && c.Id != exceptColourId, ct))
            return $"{line.Name} already has a colour called {name}.";

        // A band-priced colour takes its price from its band; an item-priced colour has
        // its own. Getting this wrong would leave the colour with no price to resolve.
        var byBand = line.Supplier!.PricingStructure == PricingStructure.Band;
        if (byBand && input.PriceBandId is null)
            return $"{line.Supplier.Name} prices by band. Choose the colour's band from the supplier's list.";
        if (!byBand && input.PriceBandId is not null)
            return $"{line.Supplier.Name} prices each colour on its own, so a colour has no band.";
        if (input.PriceBandId is int band && !await _db.PriceBands.AnyAsync(b => b.Id == band && b.ProductLineId == line.Id, ct))
            return "That band is not one of this product line's bands.";

        return null;
    }

    private static void ApplyColour(Colour colour, ColourInput input)
    {
        colour.Name = input.Name.Trim();
        colour.SupplierCode = Clean(input.SupplierCode) ?? "";
        colour.Range = Clean(input.Range);
        colour.PriceBandId = input.PriceBandId;
    }

    private static string? ValidateSupplier(SupplierInput input)
    {
        var name = Clean(input.Name);
        if (name is null)
            return "Enter the supplier's name.";
        if (name.Length > MaxNameLength || Clean(input.TradingAs)?.Length > MaxNameLength)
            return $"Keep the name and the trading name to {MaxNameLength} characters.";
        if (!Enum.IsDefined(input.PricingStructure))
            return "Choose how the supplier prices.";
        if (input.AdhesivePrice < 0)
            return "The adhesive price cannot be negative.";
        if (Clean(input.DeliveryTerms)?.Length > MaxDeliveryTermsLength)
            return $"Keep the delivery terms to {MaxDeliveryTermsLength} characters.";
        return null;
    }

    private static void ApplySupplier(Supplier supplier, SupplierInput input)
    {
        supplier.Name = input.Name.Trim();
        supplier.TradingAs = Clean(input.TradingAs);
        supplier.PricingStructure = input.PricingStructure;
        supplier.PriceListDated = input.PriceListDated;
        supplier.AdhesivePrice = input.AdhesivePrice;
        supplier.DeliveryTerms = Clean(input.DeliveryTerms) ?? "";
    }

    private static string Describe(PricingStructure structure) =>
        structure == PricingStructure.Band ? "band" : "colour";

    // ------------------------------------------------------------ quotation terms (US-12, US-13)

    private const int MaxTermLength = 500;      // QuotationTerm.Text column
    private const int MaxWarrantyLength = 60;   // Brand warranty columns

    public async Task<IReadOnlyList<TermRow>> GetTermsAsync(CancellationToken ct = default) =>
        await _db.QuotationTerms.AsNoTracking()
            .OrderBy(t => t.Section).ThenBy(t => t.SortOrder)
            .Select(t => new TermRow(t.Id, t.Section, t.Text, t.SortOrder, t.IsActive))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<BrandRow>> GetBrandsAsync(CancellationToken ct = default) =>
        await _db.Brands.AsNoTracking()
            .OrderBy(b => b.Name)
            .Select(b => new BrandRow(b.Id, b.Name, b.MaterialWarranty, b.WorkmanshipWarranty, b.ProductLines.Count))
            .ToListAsync(ct);

    public async Task<CatalogueResult> AddTermAsync(TermSection section, string text, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(section))
            return CatalogueResult.Fail("Choose a section of the quotation.");

        var wording = Clean(text);
        if (wording is null)
            return CatalogueResult.Fail("Enter the wording.");
        if (wording.Length > MaxTermLength)
            return CatalogueResult.Fail($"Keep a line to {MaxTermLength} characters.");

        var last = await _db.QuotationTerms
            .Where(t => t.Section == section)
            .MaxAsync(t => (int?)t.SortOrder, ct) ?? 0;

        _db.QuotationTerms.Add(new QuotationTerm { Section = section, Text = wording, SortOrder = last + 1 });
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    public async Task<CatalogueResult> UpdateTermAsync(int termId, string text, CancellationToken ct = default)
    {
        var wording = Clean(text);
        if (wording is null)
            return CatalogueResult.Fail("Enter the wording, or retire the line instead.");
        if (wording.Length > MaxTermLength)
            return CatalogueResult.Fail($"Keep a line to {MaxTermLength} characters.");

        var term = await _db.QuotationTerms.FindAsync(new object[] { termId }, ct);
        if (term is null)
            return CatalogueResult.Fail("That line is not in the quotation terms.");
        if (!term.IsActive)
            return CatalogueResult.Fail("That line has been retired. Add a new line instead.");

        // Quotes already approved keep the wording recorded on their version;
        // the change applies to quotations from now on.
        term.Text = wording;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    public async Task<CatalogueResult> RetireTermAsync(int termId, CancellationToken ct = default)
    {
        var term = await _db.QuotationTerms.FindAsync(new object[] { termId }, ct);
        if (term is null)
            return CatalogueResult.Fail("That line is not in the quotation terms.");
        if (!term.IsActive)
            return CatalogueResult.Ok();

        // A version cannot be approved with no standing terms at all.
        if (!await _db.QuotationTerms.AnyAsync(t => t.IsActive && t.Id != termId, ct))
            return CatalogueResult.Fail("A quotation needs at least one standing term. Add another line before retiring this one.");

        term.IsActive = false;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    public async Task<CatalogueResult> SetBrandWarrantyAsync(
        int brandId, string? materialWarranty, string? workmanshipWarranty, CancellationToken ct = default)
    {
        var material = Clean(materialWarranty);
        var workmanship = Clean(workmanshipWarranty);

        // Both or neither, as the database requires: half a warranty would print
        // on a quotation as if it were the whole of it.
        if ((material is null) != (workmanship is null))
            return CatalogueResult.Fail("Enter both the material and the workmanship warranty, or clear both.");
        if (material?.Length > MaxWarrantyLength || workmanship?.Length > MaxWarrantyLength)
            return CatalogueResult.Fail($"Keep each warranty to {MaxWarrantyLength} characters.");

        var brand = await _db.Brands.FindAsync(new object[] { brandId }, ct);
        if (brand is null)
            return CatalogueResult.Fail("That brand is not in the catalogue.");

        brand.MaterialWarranty = material;
        brand.WorkmanshipWarranty = workmanship;
        await _db.SaveChangesAsync(ct);
        return CatalogueResult.Ok();
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>
    /// IPriceHistory signals a broken price rule by throwing. The MD gets the rule's
    /// own message rather than an error page; the database triggers are the last line.
    /// </summary>
    private static async Task<CatalogueResult> AttemptAsync(Func<Task> change)
    {
        try
        {
            await change();
            return CatalogueResult.Ok();
        }
        catch (ArgumentException e)
        {
            var message = e.ParamName is null ? e.Message : e.Message.Replace($" (Parameter '{e.ParamName}')", "");
            return CatalogueResult.Fail(message);
        }
        catch (InvalidOperationException e)
        {
            return CatalogueResult.Fail(e.Message);
        }
        catch (DbUpdateException)
        {
            return CatalogueResult.Fail(
                "The database refused this price because it would overlap another period or not be greater than zero.");
        }
    }
}