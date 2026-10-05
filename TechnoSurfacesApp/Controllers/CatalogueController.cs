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

        var rows = all.AsEnumerable();
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
        return View(new PriceVm
        {
            Row = row,
            History = history,
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

public sealed class PriceVm
{
    public CatalogueRow Row { get; init; } = null!;
    public IReadOnlyList<PricePeriodRow> History { get; init; } = [];
    public bool CanEdit { get; init; }
    public DateOnly Today { get; init; }

    /// <summary>A band price applies to every colour in the band.</summary>
    public bool IsBandPrice => Row.PriceBandId is not null;
}
