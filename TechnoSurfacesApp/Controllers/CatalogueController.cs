using Microsoft.AspNetCore.Mvc;
using TechnoSurfacesApp.Data;
using TechnoSurfaces.Models;
using TechnoSurfaces.Services;
using TechnoSurfacesApp.Controllers;
using TechnoSurfacesApp.Models;
using static System.Collections.Specialized.BitVector32;
using TechnoSurfacesApp.Identity;
using Microsoft.AspNetCore.Authorization;
using TechnoSurfaces.Application.Catalogue;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// The material catalogue. Everyone can browse it and see pricing - the client
/// was explicit that all staff see all prices. Only the Managing Director can
/// change a price.
/// </summary>
public class CatalogueController : AppController
{
    private readonly ICatalogueService _catalogue;

    public CatalogueController(ICatalogueService catalogue)
        => _catalogue = catalogue;

    public async Task<IActionResult> Index(int? supplierId, int? productLineId,
        int? thickness, string? status, string? q)
    {
        var list = Db.Catalogue.AsEnumerable();

        if (supplierId is > 0)
            list = list.Where(c => c.SupplierId == supplierId);

        if (productLineId is > 0)
            list = list.Where(c => c.ProductLineId == productLineId);

        if (thickness is > 0)
            list = list.Where(c => c.ThicknessMm == thickness);

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<CatalogueStatus>(status, out var st))
            list = list.Where(c => c.Status == st);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            list = list.Where(c =>
                c.ColourName.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                c.SupplierCode.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (c.Range ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) ||
                (c.BandCode ?? "").Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        ViewData["Title"] = "Material catalogue";
        ViewData["Page"] = "catalogue";
        ViewData["Crumb"] = "Data";

        return View(new CatalogueVm
        {
            Entries = list
                .OrderBy(c => Db.SupplierName(c.SupplierId))
                .ThenBy(c => Db.ProductLineName(c.ProductLineId))
                .ThenBy(c => c.ColourName)
                .ThenByDescending(c => c.ThicknessMm)
                .ToList(),
            SupplierId = supplierId,
            ProductLineId = productLineId,
            Thickness = thickness,
            Status = status,
            Search = q,
            CanEditPrices = await CanAsync(Policies.CanEditCatalogue)
        });
    }

    /// <summary>
    /// Price maintenance. Restricted to the Managing Director - the client
    /// confirmed he is the only person who may change a material price.
    /// </summary>
    public async Task<IActionResult> Price(int id)
    {
        var entry = Db.GetEntry(id);
        if (entry is null) return RedirectToAction(nameof(Index));

        // Quotes that already carry this material. Each holds the price it
        // resolved at the time, which is why changing this price cannot
        // retroactively alter them.
        var usedBy = Db.Quotes
            .Where(x => x.MaterialLines.Any(m => m.CatalogueEntryId == id))
            .OrderByDescending(x => x.IssueDate)
            .ToList();

        ViewData["Title"] = "Material price";
        ViewData["Page"] = "catalogue";
        ViewData["Crumb"] = $"Data \u203A {Db.SupplierName(entry.SupplierId)}";

        return View(new PriceVm
        {
            Entry = entry,
            Supplier = Db.GetSupplier(entry.SupplierId)!,
            ProductLine = Db.GetProductLine(entry.ProductLineId)!,
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            UsedBy = usedBy,
            History = Db.Audit
                .Where(a => a.EntityType == "Catalogue" &&
                            a.EntityRef.Contains(entry.ColourName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.When)
                .ToList()
        });
    }

    // ======================================================================
    //  Catalogue changes - Managing Director only (US-23, US-24, NFR-10).
    //  The rules live in CatalogueService and IPriceHistory; these actions
    //  only validate input, call the service and report the outcome.
    // ======================================================================

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> SetMaterialPrice(SetMaterialPriceForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.SetMaterialPriceAsync(form.ColourId, form.PriceBandId, form.SheetSizeId,
                    form.PricePerSqm!.Value, form.EffectiveFrom!.Value)
                : CatalogueResult.Fail(FirstError()),
            "Price saved. Existing quotes keep the price they were created with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> SetRate(SetRateForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.SetRateAsync(form.RateItemId, form.SupplierId, form.Amount!.Value, form.EffectiveFrom!.Value)
                : CatalogueResult.Fail(FirstError()),
            "Rate saved. It applies to new quotes from its effective date.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireColour(int colourId) =>
        Outcome(await _catalogue.RetireColourAsync(colourId),
            "Colour retired. It stays on existing quotes but cannot be chosen on new ones.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireProductLine(int productLineId) =>
        Outcome(await _catalogue.RetireProductLineAsync(productLineId),
            "Product line retired. It stays on existing quotes but cannot be chosen on new ones.");

    // Quotation terms and brand warranties (US-12, US-13): the same policy, since
    // the MD maintains everything printed on a quotation.

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddTerm(AddTermForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddTermAsync(form.Section, form.Text!)
                : CatalogueResult.Fail(FirstError()),
            "Line added. New quotations print it; approved quotes keep the wording they were issued with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> UpdateTerm(UpdateTermForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.UpdateTermAsync(form.TermId, form.Text!)
                : CatalogueResult.Fail(FirstError()),
            "Line saved. Approved quotes keep the wording they were issued with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireTerm(int termId) =>
        Outcome(await _catalogue.RetireTermAsync(termId),
            "Line retired. It no longer prints on new quotations.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> SetBrandWarranty(BrandWarrantyForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.SetBrandWarrantyAsync(form.BrandId, form.MaterialWarranty, form.WorkmanshipWarranty)
                : CatalogueResult.Fail(FirstError()),
            "Warranty saved.");

    private string FirstError() =>
        ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
        ?? "Check the values entered.";

    /// <summary>Shows the outcome on the page the change came from.</summary>
    private IActionResult Outcome(CatalogueResult result, string success)
    {
        TempData[result.Succeeded ? "CatalogueMessage" : "CatalogueError"] =
            result.Succeeded ? success : result.Error;

        // A Referer header can be forged, so only a local page is accepted.
        var back = Request.Headers.Referer.ToString();
        return Url.IsLocalUrl(back) ? Redirect(back) : RedirectToAction(nameof(Index));
    }
}

// ==========================================================================
//  View models
// ==========================================================================

public class CatalogueVm
{
    public List<CatalogueEntry> Entries { get; set; } = new();
    public int? SupplierId { get; set; }
    public int? ProductLineId { get; set; }
    public int? Thickness { get; set; }
    public string? Status { get; set; }
    public string? Search { get; set; }
    public bool CanEditPrices { get; set; }

    public bool AnyFilter =>
        SupplierId > 0 || ProductLineId > 0 || Thickness > 0 ||
        !string.IsNullOrEmpty(Status) || !string.IsNullOrWhiteSpace(Search);

    /// <summary>Product lines for the filter, narrowed to the chosen supplier.</summary>
    public List<ProductLine> ProductLineOptions =>
        SupplierId is > 0
            ? Db.ProductLinesFor(SupplierId.Value)
            : Db.ProductLines.OrderBy(p => Db.SupplierName(p.SupplierId)).ThenBy(p => p.Name).ToList();

    public List<int> ThicknessOptions =>
        Db.Catalogue.Select(c => c.ThicknessMm).Distinct().OrderByDescending(t => t).ToList();

    public int StaleSupplierCount => Db.Suppliers.Count(s => s.IsStale);
}

public class PriceVm
{
    public CatalogueEntry Entry { get; set; } = null!;
    public Supplier Supplier { get; set; } = null!;
    public ProductLine ProductLine { get; set; } = null!;
    public bool CanEdit { get; set; }
    public List<Quote> UsedBy { get; set; } = new();
    public List<AuditEntry> History { get; set; } = new();

    /// <summary>Other sizes and thicknesses of the same colour, which price separately.</summary>
    public List<CatalogueEntry> Siblings =>
        Db.VariantsFor(Entry.ProductLineId, Entry.ColourName)
          .Where(e => e.Id != Entry.Id)
          .ToList();

    /// <summary>
    /// The price each quote resolved at. Where it differs from the current
    /// catalogue price, that is the system working as intended.
    /// </summary>
    public decimal? ResolvedOn(Quote q) =>
        q.MaterialLines.FirstOrDefault(m => m.CatalogueEntryId == Entry.Id)?.ResolvedPricePerSheet;
}