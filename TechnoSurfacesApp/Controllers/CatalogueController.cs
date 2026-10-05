using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Models;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// The material catalogue and the price editor (US-23, US-24). Everyone can browse it
/// and see every price, as the client confirmed. Only the Managing Director changes a
/// price, and a change starts on a date rather than overwriting the old price, so a
/// quote keeps the price it was made with.
/// </summary>
public class CatalogueController : AppController
{
    /// <summary>A price list older than this is flagged, so nobody quotes from it unknowingly.</summary>
    public const int StaleAfterDays = 365;

    private readonly ICatalogueService _catalogue;

    public CatalogueController(ICatalogueService catalogue) => _catalogue = catalogue;

    public async Task<IActionResult> Index(string? supplier, string? q, bool retired, CancellationToken ct)
    {
        var today = Today;
        var all = await _catalogue.GetCatalogueAsync(today, ct);

        // A colour is listed in every sheet size of its range, but a supplier may sell
        // it in only one. The sizes it has no price in are left out, unless it has no
        // price in any size, which the list must still show.
        var priced = all.Where(r => r.PricePerSqm is not null).Select(r => r.ColourId).ToHashSet();
        var rows = all.Where(r => r.PricePerSqm is not null || !priced.Contains(r.ColourId));
        if (!retired)
            rows = rows.Where(r => !r.IsRetired);
        if (!string.IsNullOrEmpty(supplier))
            rows = rows.Where(r => r.Supplier == supplier);
        if (!string.IsNullOrWhiteSpace(q))
        {
            var term = q.Trim();
            rows = rows.Where(r =>
                r.Colour.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.SupplierCode.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                r.ProductLine.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                (r.Band ?? "").Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        SetPage("Material catalogue", "catalogue", new Crumb("Data"));
        return View(new CatalogueVm
        {
            Rows = rows.ToList(),
            Suppliers = all.Select(r => r.Supplier).Distinct().Order().ToList(),
            Supplier = supplier,
            Search = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
            IncludeRetired = retired,
            StaleSuppliers = all
                .Where(r => r.PriceFrom is { } from && today.DayNumber - from.DayNumber > StaleAfterDays)
                .GroupBy(r => r.Supplier)
                .Select(g => (g.Key, g.Max(r => r.PriceFrom!.Value)))
                .OrderBy(s => s.Key)
                .ToList(),
            Today = today
        });
    }

    /// <summary>
    /// One material in one sheet size: its price now, every earlier price, and, for the
    /// Managing Director, the form that starts a new price from a date.
    /// </summary>
    public async Task<IActionResult> Price(int colourId, int sheetSizeId, CancellationToken ct)
    {
        var row = (await _catalogue.GetCatalogueAsync(Today, ct))
            .FirstOrDefault(r => r.ColourId == colourId && r.SheetSizeId == sheetSizeId);
        if (row is null)
        {
            Flash("error", "Material not found", "Choose the material from the catalogue instead.");
            return RedirectToAction(nameof(Index));
        }

        // A band-priced supplier prices the band, not the colour.
        var history = row.PriceBandId is int band
            ? await _catalogue.GetPriceHistoryAsync(null, band, sheetSizeId, ct)
            : await _catalogue.GetPriceHistoryAsync(colourId, null, sheetSizeId, ct);

        SetPage(row.Colour, "catalogue", new Crumb("Data"), new Crumb("Material catalogue", Url.Action(nameof(Index))));
        // Task 1 2.4: the quotes that use this material and the price each one locked
        // in, so the Managing Director can see a change today does not move them.
        var quotes = row.PriceBandId is int bandId
            ? await _catalogue.GetQuotesUsingPriceAsync(null, bandId, sheetSizeId, ct)
            : await _catalogue.GetQuotesUsingPriceAsync(colourId, null, sheetSizeId, ct);

        return View(new PriceVm
        {
            Row = row,
            History = history,
            Quotes = quotes,
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Today = Today
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
            "Price saved", "Quotes already made keep the price they were made with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> SetRate(SetRateForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.SetRateAsync(form.RateItemId, form.SupplierId, form.Amount!.Value, form.EffectiveFrom!.Value)
                : CatalogueResult.Fail(FirstError()),
            "Rate saved", "New quotes use it from its start date. Quotes already made keep their rate.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireColour(int colourId) =>
        Outcome(await _catalogue.RetireColourAsync(colourId),
            "Colour retired", "It stays on quotes already made, but cannot be chosen on new ones.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireProductLine(int productLineId) =>
        Outcome(await _catalogue.RetireProductLineAsync(productLineId),
            "Product line retired", "It stays on quotes already made, but cannot be chosen on new ones.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> ReinstateColour(int colourId) =>
        Outcome(await _catalogue.ReinstateColourAsync(colourId),
            "Colour reinstated", "It can be chosen on new quotes again.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> ReinstateProductLine(int productLineId) =>
        Outcome(await _catalogue.ReinstateProductLineAsync(productLineId),
            "Product line reinstated", "Its colours that are not retired can be chosen on new quotes again.");

    // ======================================================================
    //  Suppliers and what they sell (NFR-10): add, correct, retire and
    //  reinstate. Everyone can read these screens; only the Managing
    //  Director changes them, and every change is audited.
    // ======================================================================

    public async Task<IActionResult> Suppliers(CancellationToken ct)
    {
        SetPage("Suppliers", "catalogue", new Crumb("Data"), new Crumb("Material catalogue", Url.Action(nameof(Index))));
        return View(new SuppliersVm
        {
            Suppliers = await _catalogue.GetSuppliersAsync(ct),
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Today = Today
        });
    }

    public async Task<IActionResult> Supplier(int id, CancellationToken ct)
    {
        var supplier = await _catalogue.GetSupplierAsync(id, ct);
        if (supplier is null)
        {
            Flash("error", "Supplier not found", "Choose the supplier from the list instead.");
            return RedirectToAction(nameof(Suppliers));
        }

        SetPage(supplier.Name, "catalogue", new Crumb("Data"),
            new Crumb("Material catalogue", Url.Action(nameof(Index))), new Crumb("Suppliers", Url.Action(nameof(Suppliers))));
        return View(new SupplierVm
        {
            Supplier = supplier,
            Brands = await _catalogue.GetBrandsAsync(ct),
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Today = Today
        });
    }

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddSupplier(SupplierForm form)
    {
        var result = ModelState.IsValid
            ? await _catalogue.AddSupplierAsync(ToInput(form))
            : CatalogueResult.Fail(FirstError());

        if (!result.Succeeded)
        {
            Flash("error", "Not saved", result.Error);
            return RedirectToAction(nameof(Suppliers));
        }

        Flash("success", "Supplier added", "Add its product lines, sheet sizes and colours, then their prices.");
        return RedirectToAction(nameof(Supplier), new { id = result.Id });
    }

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> UpdateSupplier(int supplierId, SupplierForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.UpdateSupplierAsync(supplierId, ToInput(form))
                : CatalogueResult.Fail(FirstError()),
            "Supplier saved", "A new price list date clears the warning about an old list.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddProductLine(ProductLineForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddProductLineAsync(form.SupplierId, form.Name!, form.ThicknessMm!.Value, form.BrandId)
                : CatalogueResult.Fail(FirstError()),
            "Product line added", "Add its sheet sizes and colours next.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddSheetSize(SheetSizeForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddSheetSizeAsync(form.ProductLineId, form.LengthMm!.Value, form.WidthMm!.Value)
                : CatalogueResult.Fail(FirstError()),
            "Sheet size added", "Give each colour a price at this size from its price history.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddPriceBand(PriceBandForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddPriceBandAsync(form.ProductLineId, form.Code!, form.Name ?? "")
                : CatalogueResult.Fail(FirstError()),
            "Price band added", "Put colours in the band, then give the band its price.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddColour(ColourForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddColourAsync(form.ProductLineId, ToInput(form))
                : CatalogueResult.Fail(FirstError()),
            "Colour added", "A colour in a band takes the band's price. Any other colour needs its own price, set from its price history.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> UpdateColour(ColourForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.UpdateColourAsync(form.ColourId, ToInput(form))
                : CatalogueResult.Fail(FirstError()),
            "Colour saved", "Quotes already made keep the description and price they were made with.");

    private static SupplierInput ToInput(SupplierForm f) =>
        new(f.Name!, f.TradingAs, f.PricingStructure!.Value, f.PriceListDated!.Value, f.AdhesivePrice, f.DeliveryTerms);

    private static ColourInput ToInput(ColourForm f) =>
        new(f.Name!, f.SupplierCode, f.Range, f.PriceBandId);

    // Quotation terms and brand warranties (US-12, US-13): the same policy, since
    // the MD maintains everything printed on a quotation.

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> AddTerm(AddTermForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.AddTermAsync(form.Section, form.Text!)
                : CatalogueResult.Fail(FirstError()),
            "Line added", "New quotations print it. Approved quotes keep the wording they were issued with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> UpdateTerm(UpdateTermForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.UpdateTermAsync(form.TermId, form.Text!)
                : CatalogueResult.Fail(FirstError()),
            "Line saved", "Approved quotes keep the wording they were issued with.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> RetireTerm(int termId) =>
        Outcome(await _catalogue.RetireTermAsync(termId),
            "Line retired", "It no longer prints on new quotations.");

    [HttpPost]
    [Authorize(Policy = Policies.CanEditCatalogue)]
    public async Task<IActionResult> SetBrandWarranty(BrandWarrantyForm form) =>
        Outcome(ModelState.IsValid
                ? await _catalogue.SetBrandWarrantyAsync(form.BrandId, form.MaterialWarranty, form.WorkmanshipWarranty)
                : CatalogueResult.Fail(FirstError()),
            "Warranty saved", "New quotations for this brand print it.");

    private string FirstError() =>
        ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage).FirstOrDefault()
        ?? "Check the values entered.";

    /// <summary>Shows the outcome as a message on the page the change came from.</summary>
    private IActionResult Outcome(CatalogueResult result, string title, string detail)
    {
        if (result.Succeeded)
            Flash("success", title, detail);
        else
            Flash("error", "Not saved", result.Error);

        // A Referer header can be forged, so only a local page is accepted.
        var back = Request.Headers.Referer.ToString();
        return Url.IsLocalUrl(back) ? Redirect(back)
            : Uri.TryCreate(back, UriKind.Absolute, out var uri) && uri.Host == Request.Host.Host
                ? Redirect(uri.PathAndQuery)
                : RedirectToAction(nameof(Index));
    }
}

// ==========================================================================
//  View models
// ==========================================================================

public sealed class CatalogueVm
{
    public IReadOnlyList<CatalogueRow> Rows { get; init; } = [];
    public IReadOnlyList<string> Suppliers { get; init; } = [];
    public string? Supplier { get; init; }
    public string? Search { get; init; }
    public bool IncludeRetired { get; init; }

    /// <summary>Suppliers whose newest price is over a year old, with that date.</summary>
    public IReadOnlyList<(string Supplier, DateOnly NewestPrice)> StaleSuppliers { get; init; } = [];

    public DateOnly Today { get; init; }

    public bool AnyFilter => Supplier is not null || Search is not null || IncludeRetired;
}

public sealed class SuppliersVm
{
    public IReadOnlyList<SupplierRow> Suppliers { get; init; } = [];
    public bool CanEdit { get; init; }
    public DateOnly Today { get; init; }

    public bool IsStale(DateOnly listDated) => Today.DayNumber - listDated.DayNumber > CatalogueController.StaleAfterDays;
}

public sealed class SupplierVm
{
    public SupplierDetail Supplier { get; init; } = null!;
    public IReadOnlyList<BrandRow> Brands { get; init; } = [];
    public bool CanEdit { get; init; }
    public DateOnly Today { get; init; }

    public bool IsStale => Today.DayNumber - Supplier.PriceListDated.DayNumber > CatalogueController.StaleAfterDays;
}

public sealed class PriceVm
{
    public CatalogueRow Row { get; init; } = null!;
    public IReadOnlyList<PricePeriodRow> History { get; init; } = [];

    /// <summary>Costing lines priced from this colour or band at this size, with the price each kept.</summary>
    public IReadOnlyList<QuoteUsingPrice> Quotes { get; init; } = [];
    public bool CanEdit { get; init; }
    public DateOnly Today { get; init; }

    /// <summary>A band price applies to every colour in the band.</summary>
    public bool IsBandPrice => Row.PriceBandId is not null;
}
