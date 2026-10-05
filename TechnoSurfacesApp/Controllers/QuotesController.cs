using System.ComponentModel.DataAnnotations;
using System.Globalization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfacesApp.Api;
using TechnoSurfacesApp.Helpers;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Models;
using TechnoSurfacesApp.Services;
using QuoteStatus = TechnoSurfaces.Domain.QuoteStatus;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// The quote screens. Pages are read here through the application services. Every
/// change a user makes (a costing line, submit, approve, send, accept, reopen, the
/// invoice) is sent from the page to the /api endpoints, which hold the permission
/// checks and the error responses, so those rules exist in one place only.
/// </summary>
public class QuotesController : AppController
{
    private readonly IQuoteWorkflowService _workflow;
    private readonly IQuoteRepository _quotes;
    private readonly ICostingSheetService _costing;
    private readonly IQuotationGenerationService _quotation;
    private readonly IInvoiceRecordService _invoices;
    private readonly ICustomerService _customers;
    private readonly ICatalogueBrowser _catalogue;
    private readonly IPriceResolver _prices;
    private readonly IRateResolver _rates;
    private readonly SignedInUser _me;

    public QuotesController(
        IQuoteWorkflowService workflow,
        IQuoteRepository quotes,
        ICostingSheetService costing,
        IQuotationGenerationService quotation,
        IInvoiceRecordService invoices,
        ICustomerService customers,
        ICatalogueBrowser catalogue,
        IPriceResolver prices,
        IRateResolver rates,
        SignedInUser me)
    {
        _workflow = workflow;
        _quotes = quotes;
        _costing = costing;
        _quotation = quotation;
        _invoices = invoices;
        _customers = customers;
        _catalogue = catalogue;
        _prices = prices;
        _rates = rates;
        _me = me;
    }

    private Crumb QuotesCrumb => new("Quotes", Url.Action(nameof(Index)));

    // ======================================================================
    //  Quote list
    // ======================================================================

    public async Task<IActionResult> Index(string? status, int? customerId, bool mine, string? q, CancellationToken ct)
    {
        var filter = new QuoteListFilter(
            StatusText.All.Contains(status) ? status : null,
            customerId is > 0 ? customerId : null,
            mine ? _me.Id : null,
            string.IsNullOrWhiteSpace(q) ? null : q.Trim());

        var vm = new QuoteListVm
        {
            Quotes = await _workflow.ListAsync(filter, ct),
            Customers = await _customers.ListAsync(includeInactive: true, ct: ct),
            Status = filter.Status,
            CustomerId = filter.CustomerId,
            Mine = mine,
            Search = filter.Search
        };

        SetPage("Quotes", "quotes");
        return View(vm);
    }

    // ======================================================================
    //  New quote
    // ======================================================================

    [HttpGet]
    public async Task<IActionResult> Create(int? customerId, CancellationToken ct)
    {
        SetPage("New quote", "new", QuotesCrumb);
        return View(await NewQuoteVmAsync(new NewQuoteForm { CustomerId = customerId }, ct));
    }

    [HttpPost]
    public async Task<IActionResult> Create([Bind(Prefix = "Form")] NewQuoteForm form, CancellationToken ct)
    {
        if (ModelState.IsValid)
        {
            var result = await _workflow.CreateAsync(new NewQuote(
                form.Reference!.Trim(), form.CustomerId!.Value, form.ContactId!.Value, form.Markup(),
                form.Site, form.Project, form.CustomerReference, form.DeliveryAddress,
                form.ValidForDays ?? Quote.DefaultValidForDays), ct);

            switch (result.Outcome)
            {
                case WorkflowOutcome.Ok:
                    Flash("success", $"Quote {result.Quote!.Reference} started",
                        "Add the materials and labour. The price of each material fills in from the supplier's price list.");
                    return RedirectToAction(nameof(Costing), new { id = result.Quote.Id });

                case WorkflowOutcome.ReferenceTaken:
                    ModelState.AddModelError("Form." + nameof(NewQuoteForm.Reference),
                        result.Problem ?? "Another quote already uses this reference.");
                    break;

                case WorkflowOutcome.Invalid:
                    foreach (var (field, messages) in result.Errors!)
                        foreach (var message in messages)
                            ModelState.AddModelError("Form." + field, message);
                    break;

                default:
                    ModelState.AddModelError(string.Empty, result.Problem ?? "The quote could not be created.");
                    break;
            }
        }

        SetPage("New quote", "new", QuotesCrumb);
        return View(await NewQuoteVmAsync(form, ct));
    }

    private async Task<NewQuoteVm> NewQuoteVmAsync(NewQuoteForm form, CancellationToken ct)
    {
        var recent = await _workflow.ListAsync(new QuoteListFilter(), ct);
        return new NewQuoteVm
        {
            Form = form,
            Customers = await _customers.ChoicesForNewQuoteAsync(ct),
            LastReference = recent.OrderByDescending(r => r.Id).Select(r => r.Reference).FirstOrDefault()
        };
    }

    // ======================================================================
    //  Costing sheet: the internal costing, with the material cascade
    // ======================================================================

    public async Task<IActionResult> Costing(int id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        var costing = await _costing.GetAsync(id, ct);
        if (detail is null || costing.Outcome != CostingOutcome.Ok)
            return QuoteNotFound(id);

        var sheet = CostingSheetDto.From(costing.Quote!, costing.Totals!);
        var vm = new CostingVm
        {
            Quote = detail,
            Sheet = sheet,
            Actions = await ActionsForAsync(costing.Quote!),
            Suppliers = await _catalogue.SuppliersAsync(ct),
            RateGrid = await RateGridAsync(costing.Quote!.IssueDate, sheet, ct),
            Check = await _quotation.CheckAsync(id, ct)
        };

        SetPage("Costing sheet", "quotes", QuotesCrumb, new Crumb(detail.Reference));
        return View(vm);
    }

    /// <summary>
    /// The parts of the costing sheet that a change can move, rendered on their own:
    /// both line tables, the quotation check and the next-step buttons. The sheet
    /// swaps them in after every change, because one change can move other lines (a
    /// line calculated from the total area follows the materials) and can make a
    /// step possible, such as submitting once the first line is added.
    /// </summary>
    public async Task<IActionResult> CostingLines(int id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        var costing = await _costing.GetAsync(id, ct);
        if (detail is null || costing.Outcome != CostingOutcome.Ok)
            return NotFound();

        var actions = await ActionsForAsync(costing.Quote!);
        var sheet = CostingSheetDto.From(costing.Quote!, costing.Totals!);
        return PartialView("_CostingRefresh", new CostingRefreshVm(
            new CostingLinesVm(sheet, actions.CanEdit, await RateGridAsync(costing.Quote!.IssueDate, sheet, ct)),
            new QuoteActionBar(detail, actions, await _quotation.CheckAsync(id, ct))));
    }

    /// <summary>
    /// The whole rate card as the costing sheet lists it: every item that can be
    /// quoted, its rate-card price as at the quote's issue date, and the lines already
    /// on the quote for it. The estimator types quantities straight into this list,
    /// as on the spreadsheet it replaces, instead of adding items one at a time.
    /// </summary>
    private async Task<IReadOnlyList<RateGridRow>> RateGridAsync(DateOnly issueDate, CostingSheetDto sheet, CancellationToken ct)
    {
        var items = await _catalogue.RateItemsAsync(ct);
        var rateLines = sheet.Lines.Where(l => l.LineType == "Rate").ToList();

        var rows = new List<RateGridRow>();
        foreach (var item in items)
        {
            var card = PricePreviewResult.From(await _rates.ResolveAsync(item.Id, supplierId: null, issueDate, ct), issueDate);
            // The same rule the calculator applies, so the figure shown before the
            // line is added is the one it is added with.
            var calculated = Enum.TryParse<DerivationRule>(item.Derivation, out var rule)
                ? QuoteCalculationService.DerivedQuantity(rule, item.DerivationFactor, sheet.Totals.TotalAreaM2, sheet.Totals.TotalSheetCount)
                : null;
            rows.Add(new RateGridRow(item, card, calculated, rateLines.Where(l => l.RateItemId == item.Id).ToList()));
        }

        // Lines for an item since retired from the rate card still show, so they can be changed or removed.
        var listed = items.Select(i => i.Id).ToHashSet();
        foreach (var line in rateLines.Where(l => l.RateItemId is not int itemId || !listed.Contains(itemId)))
        {
            var item = new RateItemOption(line.RateItemId ?? 0, line.Description, "Retired", "Each", "Entered", line.IsBelowTheLine);
            rows.Add(new RateGridRow(item, new PricePreviewResult(true, line.ResolvedUnitPrice, line.PriceOrigin, null, Fmt.Date(issueDate)), null, [line]));
        }

        return rows;
    }

    /// <summary>
    /// The price a material would be charged at on this quote, before the line is
    /// added, so the estimator sees the figure, where it came from, or the reason it
    /// cannot be found as soon as the sheet size is chosen (US-01, US-03, NFR-01).
    /// Read only: the line is still added through the costing API, which resolves the
    /// price again and refuses the line if it does not resolve.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> PricePreview(int id, int colourId, int sheetSizeId, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(id, ct);
        if (quote is null)
            return NotFound();

        var resolution = await _prices.ResolveAsync(new PriceKey(colourId, sheetSizeId), quote.IssueDate, ct);
        return Json(PricePreviewResult.From(resolution, quote.IssueDate));
    }

    /// <summary>The rate-card price a labour or extras line would be charged at on this quote.</summary>
    [HttpGet]
    public async Task<IActionResult> RatePreview(int id, int rateItemId, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(id, ct);
        if (quote is null)
            return NotFound();

        var resolution = await _rates.ResolveAsync(rateItemId, supplierId: null, quote.IssueDate, ct);
        return Json(PricePreviewResult.From(resolution, quote.IssueDate));
    }

    // ======================================================================
    //  Customer quotation: the document the customer receives
    // ======================================================================

    public async Task<IActionResult> Quotation(int id, CancellationToken ct)
    {
        var document = await _quotation.GenerateAsync(id, ct);
        var detail = await DetailAsync(id, ct);
        var quote = await _quotes.GetAsync(id, ct);
        if (document is null || detail is null || quote is null)
            return QuoteNotFound(id);

        var vm = new QuotationVm
        {
            Quote = detail,
            Document = document,
            Check = await _quotation.CheckAsync(id, ct),
            Actions = await ActionsForAsync(quote)
        };

        SetPage("Customer quotation", "quotes", QuotesCrumb,
            new Crumb(document.Header.Reference, Url.Action(nameof(Costing), new { id })));
        return View(vm);
    }

    // ======================================================================
    //  Approval queue and review
    // ======================================================================

    public async Task<IActionResult> Approvals(CancellationToken ct)
    {
        var vm = new ApprovalsVm
        {
            Queue = await _workflow.ApprovalQueueAsync(ct),
            CanApprove = await CanAsync(Policies.CanApproveQuote)
        };

        SetPage("Approval queue", "approvals");
        return View(vm);
    }

    public async Task<IActionResult> Review(int id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        var costing = await _costing.GetAsync(id, ct);
        if (detail is null || costing.Outcome != CostingOutcome.Ok)
            return QuoteNotFound(id);

        var vm = new ReviewVm
        {
            Quote = detail,
            Sheet = CostingSheetDto.From(costing.Quote!, costing.Totals!),
            Check = await _quotation.CheckAsync(id, ct),
            Actions = await ActionsForAsync(costing.Quote!)
        };

        SetPage("Review quote", "approvals",
            new Crumb("Approval queue", Url.Action(nameof(Approvals))), new Crumb(detail.Reference));
        return View(vm);
    }

    // ======================================================================
    //  Version history
    // ======================================================================

    public async Task<IActionResult> Versions(int id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        var versions = await _workflow.VersionsAsync(id, ct);
        var quote = await _quotes.GetAsync(id, ct);
        if (detail is null || versions is null || quote is null)
            return QuoteNotFound(id);

        SetPage("Version history", "quotes", QuotesCrumb,
            new Crumb(detail.Reference, Url.Action(nameof(Costing), new { id })));
        return View(new VersionsVm
        {
            Quote = detail,
            Versions = versions.OrderByDescending(v => v.VersionNo).ToList(),
            Actions = await ActionsForAsync(quote)
        });
    }

    /// <summary>
    /// One version of the quote, read only: its costing and the customer quotation as
    /// they were when it was issued (US-21). Earlier versions are sealed, so nothing on
    /// this page can be changed; the current version is changed on the costing sheet.
    /// </summary>
    public async Task<IActionResult> Version(int id, int number, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        var versions = await _workflow.VersionsAsync(id, ct);
        if (detail is null || versions is null)
            return QuoteNotFound(id);

        var costing = await _costing.GetVersionAsync(id, number, ct);
        var document = await _quotation.GenerateVersionAsync(id, number, ct);
        var summary = versions.FirstOrDefault(v => v.VersionNo == number);
        if (costing.Outcome != CostingOutcome.Ok || document is null || summary is null)
        {
            Flash("error", "Version not found", $"{detail.Reference} has no version {number}. Choose one from the version history.");
            return RedirectToAction(nameof(Versions), new { id });
        }

        SetPage($"Version {number}", "quotes", QuotesCrumb,
            new Crumb(detail.Reference, Url.Action(nameof(Costing), new { id })),
            new Crumb("Version history", Url.Action(nameof(Versions), new { id })));
        return View(new VersionVm
        {
            Quote = detail,
            Sheet = CostingSheetDto.From(costing.Quote!, costing.Version!, costing.Totals!),
            Document = document,
            Versions = versions.OrderBy(v => v.VersionNo).ToList(),
            This = summary
        });
    }

    // ======================================================================
    //  Sage Pastel invoice record
    // ======================================================================

    public async Task<IActionResult> RecordInvoice(int id, CancellationToken ct)
    {
        var detail = await DetailAsync(id, ct);
        if (detail is null)
            return QuoteNotFound(id);

        var existing = await _invoices.GetAsync(id, ct);

        SetPage("Invoice record", "quotes", QuotesCrumb,
            new Crumb(detail.Reference, Url.Action(nameof(Costing), new { id })));
        return View(new InvoiceVm
        {
            Quote = detail,
            Recorded = existing.Outcome == InvoiceOutcome.Ok ? existing.Invoice : null,
            CanRecord = await CanAsync(Policies.CanRecordInvoice) && detail.Status == nameof(QuoteStatus.Accepted)
        });
    }

    // ======================================================================
    //  Shared
    // ======================================================================

    private async Task<QuoteDetail?> DetailAsync(int id, CancellationToken ct)
    {
        var result = await _workflow.GetAsync(id, ct);
        return result.Outcome == WorkflowOutcome.Ok ? result.Quote : null;
    }

    /// <summary>
    /// Which buttons a quote shows. Each one follows the domain's own lifecycle, the
    /// authorisation policy for that step and the rules the workflow service applies,
    /// so the page never offers a step the server would refuse.
    /// </summary>
    private async Task<QuoteActions> ActionsForAsync(Quote quote)
    {
        var status = quote.Status;
        var isAuthor = quote.CreatedByUserId == _me.Id;
        var isManagingDirector = await CanAsync(Policies.CanApproveQuote);
        var canEditQuote = await CanAsync(quote, Policies.CanEditQuote);
        var sealedVersion = quote.CurrentVersion?.IsSealed ?? true;

        return new QuoteActions(
            CanEdit: canEditQuote && !sealedVersion,

            // The Managing Director approves their own draft directly, so they are never
            // offered the step of submitting it to themselves.
            CanSubmit: canEditQuote && QuoteLifecycle.Allows(status, QuoteTransition.Submit)
                       && !(isManagingDirector && isAuthor),

            // A draft is approved directly only by its author (Quote.Approve).
            CanApprove: isManagingDirector && QuoteLifecycle.Allows(status, QuoteTransition.Approve)
                        && (status != QuoteStatus.Draft || isAuthor),

            CanSend: QuoteLifecycle.Allows(status, QuoteTransition.Send),
            CanAccept: isManagingDirector && QuoteLifecycle.Allows(status, QuoteTransition.Accept),
            CanReopen: await CanAsync(quote, Policies.CanReopenQuote) && QuoteLifecycle.Allows(status, QuoteTransition.Reopen),
            CanRecordInvoice: await CanAsync(Policies.CanRecordInvoice) && status == QuoteStatus.Accepted,
            IsManagingDirector: isManagingDirector);
    }

    private IActionResult QuoteNotFound(int id)
    {
        Flash("error", "Quote not found", $"There is no quote {id}. Choose it from the list instead.");
        return RedirectToAction(nameof(Index));
    }
}

// ==========================================================================
//  View models
// ==========================================================================

/// <summary>What the signed-in user may do to a quote right now.</summary>
public sealed record QuoteActions(
    bool CanEdit,
    bool CanSubmit,
    bool CanApprove,
    bool CanSend,
    bool CanAccept,
    bool CanReopen,
    bool CanRecordInvoice,
    bool IsManagingDirector);

/// <summary>The lifecycle buttons for one quote, with what decides whether they can be pressed.</summary>
public sealed record QuoteActionBar(QuoteDetail Quote, QuoteActions Actions, QuotationCheck? Check);

/// <summary>The answer to a price lookup, shaped for the costing sheet's script.</summary>
public sealed record PricePreviewResult(bool Resolved, decimal? UnitPrice, string? Origin, string? Reason, string PricedAsAt)
{
    public static PricePreviewResult From(PriceResolution resolution, DateOnly asAt) =>
        resolution.Resolved
            ? new(true, resolution.UnitPrice, resolution.Origin, null, Fmt.Date(asAt))
            : new(false, null, null, resolution.FailureReason ?? "The price could not be found.", Fmt.Date(asAt));
}

public sealed class QuoteListVm
{
    public IReadOnlyList<QuoteSummary> Quotes { get; init; } = [];
    public IReadOnlyList<CustomerSummary> Customers { get; init; } = [];
    public string? Status { get; init; }
    public int? CustomerId { get; init; }
    public bool Mine { get; init; }
    public string? Search { get; init; }

    public bool AnyFilter => Status is not null || CustomerId is not null || Mine || Search is not null;
}

/// <summary>The new quote form. The limits match the checks in the quote workflow service.</summary>
public sealed class NewQuoteForm
{
    [Required(ErrorMessage = "Enter a reference for the quote.")]
    [StringLength(40, ErrorMessage = "Keep the reference to 40 characters or fewer.")]
    [Display(Name = "Quote reference")]
    public string? Reference { get; set; }

    [Required(ErrorMessage = "Choose the customer.")]
    [Display(Name = "Customer")]
    public int? CustomerId { get; set; }

    [Required(ErrorMessage = "Choose who the quote is addressed to.")]
    [Display(Name = "Attention of")]
    public int? ContactId { get; set; }

    /// <summary>
    /// Typed as text so that 37,5 and 37.5 are read the same way on every machine,
    /// whatever the server's regional settings.
    /// </summary>
    [Required(ErrorMessage = "Enter the markup for this quote.")]
    [RegularExpression(@"^\s*\d{1,3}([.,]\d{1,2})?\s*$",
        ErrorMessage = "Enter the markup as a number from 0 to 999,99, such as 35 or 37,5.")]
    [Display(Name = "Markup")]
    public string? MarkupPercent { get; set; }

    [StringLength(200, ErrorMessage = "Keep the site to 200 characters or fewer.")]
    public string? Site { get; set; }

    [StringLength(200, ErrorMessage = "Keep the project to 200 characters or fewer.")]
    public string? Project { get; set; }

    [StringLength(100, ErrorMessage = "Keep the customer's reference to 100 characters or fewer.")]
    [Display(Name = "Customer's own reference")]
    public string? CustomerReference { get; set; }

    [StringLength(300, ErrorMessage = "Keep the delivery address to 300 characters or fewer.")]
    [Display(Name = "Delivery address")]
    public string? DeliveryAddress { get; set; }

    [Required(ErrorMessage = "Enter how many days the quote is valid for.")]
    [Range(1, QuoteWorkflowService.MaxValidForDays, ErrorMessage = "Enter between 1 and 365 days.")]
    [Display(Name = "Valid for")]
    public int? ValidForDays { get; set; } = Quote.DefaultValidForDays;

    public decimal Markup() =>
        decimal.Parse(MarkupPercent!.Trim().Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
}

public sealed class NewQuoteVm
{
    public NewQuoteForm Form { get; init; } = new();
    public IReadOnlyList<CustomerChoice> Customers { get; init; } = [];
    public string? LastReference { get; init; }
}

public sealed class CostingVm
{
    public QuoteDetail Quote { get; init; } = null!;
    public CostingSheetDto Sheet { get; init; } = null!;
    public QuoteActions Actions { get; init; } = null!;
    public IReadOnlyList<SupplierOption> Suppliers { get; init; } = [];
    public IReadOnlyList<RateGridRow> RateGrid { get; init; } = [];
    public QuotationCheck? Check { get; init; }
}

public sealed record CostingLinesVm(CostingSheetDto Sheet, bool CanEdit, IReadOnlyList<RateGridRow>? RateGrid = null);

/// <summary>
/// One rate card item on the costing sheet: its rate-card price on the quote date,
/// the quantity the materials work out to when it is a calculated item, and the
/// lines already on the quote for it (normally none or one).
/// </summary>
public sealed record RateGridRow(RateItemOption Item, PricePreviewResult Card, decimal? CalculatedQuantity, IReadOnlyList<CostingLineDto> Lines)
{
    public bool IsCalculated => CalculatedQuantity is not null;
}

public sealed record CostingRefreshVm(CostingLinesVm Lines, QuoteActionBar Side);

public sealed class QuotationVm
{
    /// <summary>For the strip and tabs above the document, which are never printed.</summary>
    public QuoteDetail Quote { get; init; } = null!;

    /// <summary>The document itself. It carries no cost price, discount or markup.</summary>
    public CustomerQuotation Document { get; init; } = null!;
    public QuotationCheck? Check { get; init; }
    public QuoteActions Actions { get; init; } = null!;
}

public sealed class ApprovalsVm
{
    public IReadOnlyList<QuoteSummary> Queue { get; init; } = [];
    public bool CanApprove { get; init; }
}

public sealed class ReviewVm
{
    public QuoteDetail Quote { get; init; } = null!;
    public CostingSheetDto Sheet { get; init; } = null!;
    public QuotationCheck? Check { get; init; }
    public QuoteActions Actions { get; init; } = null!;
}

public sealed class VersionsVm
{
    public QuoteDetail Quote { get; init; } = null!;
    public IReadOnlyList<QuoteVersionSummary> Versions { get; init; } = [];
    public QuoteActions Actions { get; init; } = null!;
}

/// <summary>One version of a quote, opened from the version history.</summary>
public sealed class VersionVm
{
    /// <summary>The quote as it stands now, for the strip and tabs.</summary>
    public QuoteDetail Quote { get; init; } = null!;

    /// <summary>This version's costing. Cost figures, so never printed.</summary>
    public CostingSheetDto Sheet { get; init; } = null!;

    /// <summary>This version's customer quotation. It carries no cost, discount or markup.</summary>
    public CustomerQuotation Document { get; init; } = null!;

    public IReadOnlyList<QuoteVersionSummary> Versions { get; init; } = [];
    public QuoteVersionSummary This { get; init; } = null!;

    public bool IsCurrent => This.VersionNo == Quote.VersionNo;
    public QuoteVersionSummary? Next => Versions.FirstOrDefault(v => v.VersionNo == This.VersionNo + 1);
}

public sealed class InvoiceVm
{
    public QuoteDetail Quote { get; init; } = null!;
    public InvoiceRecordView? Recorded { get; init; }
    public bool CanRecord { get; init; }
}
