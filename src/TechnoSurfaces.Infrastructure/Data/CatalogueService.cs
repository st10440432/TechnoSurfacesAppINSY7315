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
                    price?.PricePerSqm, price?.PricePerSheet(size), price?.EffectiveFrom));
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