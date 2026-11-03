using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Models;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Customers and their contacts (US-15). Both roles keep them up to date, so these
/// screens are never read only. The pages read through the customer service; every
/// change is sent from the page to the /api/customers endpoints. Nothing is ever
/// deleted: a customer or contact is deactivated, which takes it off the new quote
/// form but keeps old quotes reading correctly. The one exception to "both roles" is
/// the import from Sage Pastel, which adds the whole customer list at once and is for
/// the Managing Director only.
/// </summary>
public class CustomersController : AppController
{
    private readonly ICustomerService _customers;
    private readonly IQuoteWorkflowService _workflow;

    public CustomersController(ICustomerService customers, IQuoteWorkflowService workflow)
    {
        _customers = customers;
        _workflow = workflow;
    }

    public async Task<IActionResult> Index(string? q, bool inactive, int? page, CancellationToken ct)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        SetPage("Customers", "customers");
        return View(new CustomerListVm
        {
            Page = await _customers.ListPageAsync(search, inactive, page ?? 1, ct),
            Search = search,
            IncludeInactive = inactive,
            CanImport = await CanAsync(Policies.CanImportCustomers)
        });
    }

    // ======================================================================
    //  Import from Sage Pastel (Managing Director only)
    // ======================================================================

    /// <summary>The largest listing accepted. The client's 1 559 customers are about 1.1 MB.</summary>
    private const long MaxListingBytes = 10 * 1024 * 1024;

    private static readonly TimeSpan PreviewLifetime = TimeSpan.FromMinutes(30);

    [Authorize(Policy = Policies.CanImportCustomers)]
    public IActionResult Import()
    {
        SetImportPage();
        return View(new CustomerImportVm());
    }

    /// <summary>
    /// Reads the uploaded listing and shows what would be added and skipped. Nothing is
    /// saved: the plan is held for this user for half an hour, until they confirm it.
    /// The file itself is not kept.
    /// </summary>
    [HttpPost]
    [Authorize(Policy = Policies.CanImportCustomers)]
    [RequestSizeLimit(MaxListingBytes)]
    public async Task<IActionResult> Import(IFormFile? listing, [FromServices] ICustomerImportService import,
        [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        SetImportPage();

        if (listing is null || listing.Length == 0)
            return View(new CustomerImportVm { Error = "Choose the Customer Listing file exported from Sage Pastel." });

        PastelListing parsed;
        await using (var stream = listing.OpenReadStream())
            parsed = await PastelCustomerListing.ReadAsync(stream, ct);

        var plan = await import.PlanAsync(parsed, ct);
        var key = Guid.NewGuid().ToString("N");
        cache.Set(PreviewKey(key), plan, PreviewLifetime);

        return View(new CustomerImportVm { Plan = plan, PreviewKey = key, FileName = Path.GetFileName(listing.FileName) });
    }

    [HttpPost]
    [Authorize(Policy = Policies.CanImportCustomers)]
    public async Task<IActionResult> ConfirmImport(string previewKey, [FromServices] ICustomerImportService import,
        [FromServices] IMemoryCache cache, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(previewKey) || !cache.TryGetValue(PreviewKey(previewKey), out CustomerImportPlan? plan) || plan is null)
        {
            Flash("error", "Nothing imported", "The preview had expired. Choose the file again and check it before importing.");
            return RedirectToAction(nameof(Import));
        }

        cache.Remove(PreviewKey(previewKey));
        var result = await import.ImportAsync(plan, ct);

        Flash("success", $"{result.CustomersAdded} customer{(result.CustomersAdded == 1 ? "" : "s")} imported",
            $"{result.ContactsAdded} contact{(result.ContactsAdded == 1 ? "" : "s")} added; {result.Skipped.Count} left out, as the preview listed.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>A preview belongs to the person who made it.</summary>
    private string PreviewKey(string key) =>
        $"customer-import:{User.FindFirstValue(ClaimTypes.NameIdentifier)}:{key}";

    private void SetImportPage() =>
        SetPage("Import from Pastel", "customers", new Crumb("Customers", Url.Action(nameof(Index))));

    public async Task<IActionResult> Details(int id, CancellationToken ct)
    {
        var result = await _customers.GetAsync(id, ct);
        if (result.Outcome != CustomerOutcome.Ok)
        {
            Flash("error", "Customer not found", $"There is no customer {id}. Choose one from the list instead.");
            return RedirectToAction(nameof(Index));
        }

        var customer = result.Customer!;
        SetPage(customer.Name, "customers", new Crumb("Customers", Url.Action(nameof(Index))));
        return View(new CustomerDetailVm
        {
            Customer = customer,
            Quotes = await _workflow.ListAsync(new QuoteListFilter(CustomerId: id), ct)
        });
    }
}

public sealed class CustomerListVm
{
    public CustomerPage Page { get; init; } = new([], 0, 1, CustomerService.ListPageSize);
    public IReadOnlyList<CustomerSummary> Customers => Page.Customers;
    public string? Search { get; init; }
    public bool IncludeInactive { get; init; }
    public bool CanImport { get; init; }
}

public sealed class CustomerImportVm
{
    public CustomerImportPlan? Plan { get; init; }
    public string? PreviewKey { get; init; }
    public string? FileName { get; init; }
    public string? Error { get; init; }
}

public sealed class CustomerDetailVm
{
    public CustomerView Customer { get; init; } = null!;
    public IReadOnlyList<QuoteSummary> Quotes { get; init; } = [];
}
