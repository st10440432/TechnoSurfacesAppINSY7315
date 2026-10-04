using Microsoft.AspNetCore.Mvc;
using System.Globalization;
using System.Text.Json;
using TechnoSurfaces.Models;
using TechnoSurfaces.Services;
using TechnoSurfacesApp.Controllers;
using TechnoSurfacesApp.Data;
using TechnoSurfacesApp.Helpers;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Models;
using static System.Collections.Specialized.BitVector32;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Quoting screens. The prototype has no back end - every action reads from the
/// in-memory demo store and nothing is written back.
/// </summary>
public class QuotesController : AppController
{
    public QuotesController(DemoSession session) : base(session) { }

    // ======================================================================
    //  Quote list
    // ======================================================================

    public IActionResult Index(string? status, int? customerId, int? ownerId, string? q)
    {
        var list = Db.Quotes.AsEnumerable();

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<QuoteStatus>(status, out var s))
            list = list.Where(x => x.Status == s);

        if (customerId is > 0)
            list = list.Where(x => x.CustomerId == customerId);

        if (ownerId is > 0)
            list = list.Where(x => x.OwnerUserId == ownerId);

        if (!string.IsNullOrWhiteSpace(q))
        {
            var t = q.Trim();
            list = list.Where(x =>
                x.Ref.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                x.Project.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                x.Site.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                Db.CustomerName(x.CustomerId).Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        ViewData["Title"] = "Quotes";
        ViewData["Page"] = "quotes";
        ViewData["Crumb"] = "Quoting";

        return View(new QuoteListVm
        {
            Quotes = list.OrderByDescending(x => x.IssueDate).ToList(),
            Status = status,
            CustomerId = customerId,
            OwnerId = ownerId,
            Search = q
        });
    }

    // ======================================================================
    //  New quote - the supplier / material / colour / size cascade
    // ======================================================================

    public IActionResult Create()
    {
        // The whole catalogue is handed to the page so the cascade can filter
        // without a round trip. Discontinued entries are excluded from new quotes
        // but stay in the store so historic quotes still resolve.
        var payload = new
        {
            suppliers = Db.Suppliers.OrderBy(s => s.Name).Select(s => new
            {
                id = s.Id,
                name = s.Name,
                tradingAs = s.TradingAs,
                structure = s.PricingStructure.ToString(),
                dated = Fmt.Date(s.PriceListDated),
                stale = s.IsStale,
                adhesive = s.AdhesivePrice,
                delivery = s.DeliveryTerms
            }),
            lines = Db.ProductLines.Select(p => new
            {
                id = p.Id,
                sup = p.SupplierId,
                name = p.Name
            }),
            entries = Db.Catalogue
                .Where(c => c.Status != CatalogueStatus.Discontinued)
                .Select(c => new
                {
                    id = c.Id,
                    sup = c.SupplierId,
                    pl = c.ProductLineId,
                    colour = c.ColourName,
                    code = c.SupplierCode,
                    band = c.BandCode,
                    range = c.Range,
                    len = c.SheetLengthMm,
                    wid = c.SheetWidthMm,
                    thk = c.ThicknessMm,
                    area = c.SheetAreaSqm,
                    sqm = c.EffectivePricePerSqm,
                    sheet = c.EffectivePricePerSheet,
                    basis = c.PriceBasis,
                    stock = c.StockQty,
                    status = c.StatusLabel,
                    eff = Fmt.Date(c.EffectiveFrom)
                }),
            customers = Db.Customers.Select(c => new
            {
                id = c.Id,
                name = c.CompanyName,
                account = c.AccountCode,
                address = c.BillingAddress,
                contacts = c.Contacts.Select(k => new
                {
                    id = k.Id,
                    name = k.Name,
                    role = k.JobTitle,
                    tel = k.Tel,
                    email = k.Email
                })
            })
        };

        ViewData["Title"] = "New quote";
        ViewData["Page"] = "new";
        ViewData["Crumb"] = "Quoting";
        ViewData["Json"] = JsonSerializer.Serialize(payload);
        ViewData["NextRef"] = $"TS-2026-{Db.Quotes.Count + 142:0000}";
        return View();
    }

    // ======================================================================
    //  Internal costing sheet
    // ======================================================================

    public IActionResult Costing(int id)
    {
        var quote = Db.GetQuote(id);
        if (quote is null) return RedirectToAction(nameof(Index));

        var me = Session.User;

        ViewData["Title"] = "Costing sheet";
        ViewData["Page"] = "quotes";
        ViewData["Crumb"] = $"Quoting \u203A {quote.Ref}";

        return View(new CostingVm
        {
            Quote = quote,
            // Approved quotes lock against estimator edits; the MD can always edit.
            CanEdit = Session.IsMd ||
                      (quote.OwnerUserId == me.Id && quote.Status == QuoteStatus.Draft)
        });
    }

    // ======================================================================
    //  Customer-facing quotation
    // ======================================================================

    public IActionResult Quotation(int id)
    {
        var quote = Db.GetQuote(id);
        if (quote is null) return RedirectToAction(nameof(Index));

        var lines = quote.QuotationLines.ToList();
        var isDraft = lines.Count == 0;

        // Where the estimator has not composed customer-facing lines yet, offer a
        // starting point derived from the costing total. Real lines are written per
        // room or element and replace these.
        if (isDraft)
        {
            lines.Add(new QuotationLine
            {
                Item = quote.Project,
                Description = "Fabricate and install. Templates by Techno Surfaces. " +
                              string.Join("; ", quote.MaterialLines.Select(m =>
                                  $"Material: {m.SupplierName}, Colour: {m.ColourName}")),
                Qty = 1m,
                Rate = Math.Round(quote.Total, 2)
            });
            lines.Add(new QuotationLine
            {
                Description = "Includes 16mm MDF support board where required."
            });
        }

        ViewData["Title"] = "Customer quotation";
        ViewData["Page"] = "quotes";
        ViewData["Crumb"] = $"Quoting \u203A {quote.Ref}";

        return View(new QuotationVm
        {
            Quote = quote,
            Lines = lines,
            LinesAreDraft = isDraft,
            Brands = quote.MaterialLines.Select(m => m.SupplierName).Distinct().ToList()
        });
    }

    // ======================================================================
    //  Approval queue
    // ======================================================================

    public async Task<IActionResult> Approvals()
    {
        ViewData["Title"] = "Approval queue";
        ViewData["Page"] = "approvals";
        ViewData["Crumb"] = "Quoting";

        return View(new ApprovalsVm
        {
            Pending = Db.QuotesAwaitingApproval,
            IsMd = await CanAsync(Policies.CanApproveQuote)
        });
    }

    public async Task<IActionResult> Review(int id)
    {
        var quote = Db.GetQuote(id);
        if (quote is null) return RedirectToAction(nameof(Approvals));

        ViewData["Title"] = "Review quote";
        ViewData["Page"] = "approvals";
        ViewData["Crumb"] = $"Quoting \u203A {quote.Ref}";

        return View(new ReviewVm
        {
            Quote = quote,
            IsMd = await CanAsync(Policies.CanApproveQuote),
            Checks = BuildChecks(quote)
        });
    }

    /// <summary>
    /// Sanity checks surfaced before approval. These are prompts for the MD's
    /// judgement, not rules - the thresholds are indicative and configurable.
    /// </summary>
    private static List<ReviewCheck> BuildChecks(Quote quote)
    {
        var checks = new List<ReviewCheck>();

        // Markup within the usual band
        if (quote.MarkupPct < 25m)
            checks.Add(new("warn", $"Markup is {Fmt.Pct(quote.MarkupPct)}",
                "Below the range normally applied. Confirm this is deliberate."));
        else if (quote.MarkupPct > 45m)
            checks.Add(new("info", $"Markup is {Fmt.Pct(quote.MarkupPct)}",
                "Above the range normally applied."));
        else
            checks.Add(new("ok", $"Markup is {Fmt.Pct(quote.MarkupPct)}",
                "Within the range normally applied."));

        // Rand per square metre
        if (quote.TotalSqm == 0)
            checks.Add(new("warn", "No material on the quote",
                "The rand per square metre check cannot run without a material line."));
        else if (quote.RandPerSqm < 2000m || quote.RandPerSqm > 12000m)
            checks.Add(new("warn", $"{Fmt.Rand(quote.RandPerSqm)} per m\u00B2",
                "Outside the usual range. Worth a second look before this goes out."));
        else
            checks.Add(new("ok", $"{Fmt.Rand(quote.RandPerSqm)} per m\u00B2",
                $"{Fmt.Area(quote.TotalSqm)} across {quote.MaterialLines.Count} material line(s)."));

        // Supplier price list age
        var stale = quote.MaterialLines
            .Select(m => Db.Suppliers.FirstOrDefault(s => s.Name == m.SupplierName))
            .Where(s => s is { IsStale: true })
            .Select(s => s!.Name)
            .Distinct()
            .ToList();

        if (stale.Any())
            checks.Add(new("warn", "Price list over a year old",
                $"{string.Join(", ", stale)} \u2014 confirm current pricing before this is sent."));
        else
            checks.Add(new("ok", "Supplier pricing is current",
                "Every material on this quote came from a price list under a year old."));

        // Catalogue lifecycle
        var retiring = quote.MaterialLines
            .Select(m => Db.GetEntry(m.CatalogueEntryId))
            .Where(e => e is not null && e.Status != CatalogueStatus.Active)
            .Select(e => e!.ColourName)
            .Distinct()
            .ToList();

        if (retiring.Any())
            checks.Add(new("warn", "Material being phased out",
                $"{string.Join(", ", retiring)} \u2014 check availability before committing."));

        // Supplier discount
        var discounted = quote.MaterialLines.Where(m => m.SupplierDiscountPct > 0).ToList();
        if (discounted.Any())
            checks.Add(new("info", "Supplier discount applied",
                $"{discounted.Count} line(s) carry a discount received, up to " +
                $"{Fmt.Pct(discounted.Max(m => m.SupplierDiscountPct))}. This reduces cost before markup."));

        // Validity
        if (quote.IsExpired)
            checks.Add(new("warn", "Quote validity has lapsed",
                $"Valid until {Fmt.Date(quote.ValidUntil)}. Reissue before sending."));
        else
            checks.Add(new("ok", "Valid for another " + quote.DaysRemaining + " days",
                $"Expires {Fmt.Date(quote.ValidUntil)}, per the 30-day standing term."));

        return checks;
    }

    // ======================================================================
    //  Version history
    // ======================================================================

    public IActionResult Versions(int id, int? a, int? b)
    {
        var quote = Db.GetQuote(id);
        if (quote is null) return RedirectToAction(nameof(Index));

        var ordered = quote.Versions.OrderBy(v => v.VersionNumber).ToList();
        var canCompare = ordered.Count > 1;

        QuoteVersion? va = null, vb = null;
        if (canCompare)
        {
            va = ordered.FirstOrDefault(v => v.VersionNumber == a) ?? ordered[^2];
            vb = ordered.FirstOrDefault(v => v.VersionNumber == b) ?? ordered[^1];
        }

        ViewData["Title"] = "Version history";
        ViewData["Page"] = "quotes";
        ViewData["Crumb"] = $"Quoting \u203A {quote.Ref}";

        return View(new VersionsVm
        {
            Quote = quote,
            CanCompare = canCompare,
            A = va,
            B = vb
        });
    }

    // ======================================================================
    //  Pastel invoice reference
    // ======================================================================

    public IActionResult RecordInvoice(int id)
    {
        var quote = Db.GetQuote(id);
        if (quote is null) return RedirectToAction(nameof(Index));

        ViewData["Title"] = "Record Pastel invoice";
        ViewData["Page"] = "quotes";
        ViewData["Crumb"] = $"Quoting \u203A {quote.Ref}";

        return View(new RecordInvoiceVm { Quote = quote });
    }
}

// ==========================================================================
//  View models
// ==========================================================================

public class QuoteListVm
{
    public List<Quote> Quotes { get; set; } = new();
    public string? Status { get; set; }
    public int? CustomerId { get; set; }
    public int? OwnerId { get; set; }
    public string? Search { get; set; }

    public bool AnyFilter =>
        !string.IsNullOrEmpty(Status) || CustomerId > 0 ||
        OwnerId > 0 || !string.IsNullOrWhiteSpace(Search);

    public decimal TotalValue => Quotes.Sum(q => q.Total);
}

public class CostingVm
{
    public Quote Quote { get; set; } = null!;

    /// <summary>
    /// Estimators edit their own drafts only. The MD edits anything, including
    /// correcting an estimator's quote before approving it.
    /// </summary>
    public bool CanEdit { get; set; }

    /// <summary>Section order on the costing sheet, matching the client's own layout.</summary>
    public static readonly RateGroup[] Order =
    {
        RateGroup.Fabrication,
        RateGroup.Consumables,
        RateGroup.Installation,
        RateGroup.WoodSubstrate,
        RateGroup.SinksHardware,
        RateGroup.BelowTheLine
    };

    /// <summary>Invariant-culture string for a number going into an input value.</summary>
    public static string Val(decimal d) => d.ToString(CultureInfo.InvariantCulture);
}

public class QuotationVm
{
    public Quote Quote { get; set; } = null!;
    public List<QuotationLine> Lines { get; set; } = new();
    public bool LinesAreDraft { get; set; }
    public List<string> Brands { get; set; } = new();

    /// <summary>
    /// Warranty wording is a rule, not free text - it follows the brand quoted.
    /// Avonite and Samsung Staron carry a one-year workmanship warranty;
    /// DuPont Corian carries ten.
    /// </summary>
    public List<(string Brand, string Material, string Workmanship)> Warranties
    {
        get
        {
            var rows = new List<(string, string, string)>();

            if (Brands.Any(b => b.Contains("Staron", StringComparison.OrdinalIgnoreCase) ||
                                b.Contains("Max on Top", StringComparison.OrdinalIgnoreCase)))
                rows.Add(("Avonite, Samsung Staron", "10 years", "1 year"));

            if (Brands.Any(b => b.Contains("Corian", StringComparison.OrdinalIgnoreCase)))
                rows.Add(("DuPont Corian", "10 years", "10 years (limited)"));

            if (rows.Count == 0)
                rows.Add(("Solid surface (as quoted)", "10 years", "1 year"));

            return rows;
        }
    }

    // Customer-facing totals. Composed from the descriptive lines, deliberately
    // independent of the costing breakdown - the customer never sees that.
    public decimal SubTotal => Lines.Sum(l => l.LineTotal);
    public decimal Discount => Math.Round(SubTotal * (Quote.CustomerDiscountPct / 100m), 2);
    public decimal ExVat => SubTotal - Discount;
    public decimal Vat => Math.Round(ExVat * 0.15m, 2);
    public decimal IncVat => ExVat + Vat;
}

public class ApprovalsVm
{
    public List<Quote> Pending { get; set; } = new();
    public bool IsMd { get; set; }

    public decimal TotalValue => Pending.Sum(q => q.Total);

    public int EstimatorCount => Pending.Select(q => q.OwnerUserId).Distinct().Count();

    public int LongestWait => Pending.Any()
        ? Pending.Max(q => (DateTime.Today - q.IssueDate).Days)
        : 0;
}

/// <summary>Level is "ok", "warn" or "info" - it maps to a CSS modifier.</summary>
public record ReviewCheck(string Level, string Title, string Detail);

public class ReviewVm
{
    public Quote Quote { get; set; } = null!;
    public bool IsMd { get; set; }
    public List<ReviewCheck> Checks { get; set; } = new();
}

public class VersionsVm
{
    public Quote Quote { get; set; } = null!;
    public bool CanCompare { get; set; }
    public QuoteVersion? A { get; set; }
    public QuoteVersion? B { get; set; }

    public decimal Delta => (B?.Total ?? 0m) - (A?.Total ?? 0m);

    public decimal DeltaPct =>
        A is null || A.Total == 0m ? 0m : Math.Round(Delta / A.Total * 100m, 1);
}

public class RecordInvoiceVm
{
    public Quote Quote { get; set; } = null!;

    public bool IsRecorded => !string.IsNullOrEmpty(Quote.PastelInvoiceNo);

    /// <summary>Invoiced amount against the quoted total including VAT.</summary>
    public decimal Variance =>
        (Quote.PastelInvoiceAmount ?? 0m) - Math.Round(Quote.Total * 1.15m, 2);
}