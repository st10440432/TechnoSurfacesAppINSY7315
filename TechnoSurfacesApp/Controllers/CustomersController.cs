using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Models;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Customers and their contacts (US-15). Both roles keep them up to date, so these
/// screens are never read only. The pages read through the customer service; every
/// change is sent from the page to the /api/customers endpoints. Nothing is ever
/// deleted: a customer or contact is deactivated, which takes it off the new quote
/// form but keeps old quotes reading correctly.
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

    public async Task<IActionResult> Index(string? q, bool inactive, CancellationToken ct)
    {
        var search = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        SetPage("Customers", "customers");
        return View(new CustomerListVm
        {
            Customers = await _customers.ListAsync(search, inactive, ct),
            Search = search,
            IncludeInactive = inactive
        });
    }

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
    public IReadOnlyList<CustomerSummary> Customers { get; init; } = [];
    public string? Search { get; init; }
    public bool IncludeInactive { get; init; }
}

public sealed class CustomerDetailVm
{
    public CustomerView Customer { get; init; } = null!;
    public IReadOnlyList<QuoteSummary> Quotes { get; init; } = [];
}
