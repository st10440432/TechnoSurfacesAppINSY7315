using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Customers;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// Customers and their contacts (US-15). Both roles maintain them, so any signed-in
/// user may call these. There is no delete: a customer or contact is deactivated,
/// which takes it off the new-quote choice while old quotes still show it.
///
/// Routes carry ids only. Contact names, phone numbers and email addresses travel in
/// request bodies, never in a URL (NFR-09).
/// </summary>
[ApiController]
[Route("api/customers")]
public sealed class CustomerRecordsController : ControllerBase
{
    private readonly ICustomerService _customers;

    public CustomerRecordsController(ICustomerService customers) => _customers = customers;

    /// <summary>GET /api/customers?search=&amp;includeInactive=: search on company name and account code.</summary>
    [HttpGet]
    [ProducesResponseType<IReadOnlyList<CustomerSummary>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(string? search, bool includeInactive, CancellationToken ct) =>
        Ok(await _customers.ListAsync(search, includeInactive, ct));

    /// <summary>GET /api/customers/choices: active customers with their active contacts, for a new quote.</summary>
    [HttpGet("choices")]
    [ProducesResponseType<IReadOnlyList<CustomerChoice>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Choices(CancellationToken ct) =>
        Ok(await _customers.ChoicesForNewQuoteAsync(ct));

    /// <summary>GET /api/customers/{id}</summary>
    [HttpGet("{id:int}", Name = nameof(GetCustomer))]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetCustomer(int id, CancellationToken ct) =>
        Respond(await _customers.GetAsync(id, ct), r => Ok(r.Customer));

    /// <summary>POST /api/customers</summary>
    [HttpPost]
    [ProducesResponseType<CustomerView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(CustomerRequest request, CancellationToken ct) =>
        Respond(await _customers.CreateAsync(request.ToInput(), ct),
            r => CreatedAtRoute(nameof(GetCustomer), new { id = r.Customer!.Id }, r.Customer));

    /// <summary>PUT /api/customers/{id}</summary>
    [HttpPut("{id:int}")]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(int id, CustomerRequest request, CancellationToken ct) =>
        Respond(await _customers.UpdateAsync(id, request.ToInput(), ct), r => Ok(r.Customer));

    /// <summary>POST /api/customers/{id}/deactivate</summary>
    [HttpPost("{id:int}/deactivate")]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Deactivate(int id, CancellationToken ct) =>
        Respond(await _customers.SetActiveAsync(id, false, ct), r => Ok(r.Customer));

    /// <summary>POST /api/customers/{id}/reactivate</summary>
    [HttpPost("{id:int}/reactivate")]
    [ProducesResponseType<CustomerView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reactivate(int id, CancellationToken ct) =>
        Respond(await _customers.SetActiveAsync(id, true, ct), r => Ok(r.Customer));

    /// <summary>POST /api/customers/{id}/contacts</summary>
    [HttpPost("{id:int}/contacts")]
    [ProducesResponseType<ContactView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddContact(int id, ContactRequest request, CancellationToken ct) =>
        Respond(await _customers.AddContactAsync(id, request.ToInput(), ct),
            r => CreatedAtRoute(nameof(GetCustomer), new { id }, r.Contact));

    /// <summary>PUT /api/customers/{id}/contacts/{contactId}</summary>
    [HttpPut("{id:int}/contacts/{contactId:int}")]
    [ProducesResponseType<ContactView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateContact(int id, int contactId, ContactRequest request, CancellationToken ct) =>
        Respond(await _customers.UpdateContactAsync(id, contactId, request.ToInput(), ct), r => Ok(r.Contact));

    /// <summary>POST /api/customers/{id}/contacts/{contactId}/deactivate</summary>
    [HttpPost("{id:int}/contacts/{contactId:int}/deactivate")]
    [ProducesResponseType<ContactView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeactivateContact(int id, int contactId, CancellationToken ct) =>
        Respond(await _customers.SetContactActiveAsync(id, contactId, false, ct), r => Ok(r.Contact));

    /// <summary>POST /api/customers/{id}/contacts/{contactId}/reactivate</summary>
    [HttpPost("{id:int}/contacts/{contactId:int}/reactivate")]
    [ProducesResponseType<ContactView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReactivateContact(int id, int contactId, CancellationToken ct) =>
        Respond(await _customers.SetContactActiveAsync(id, contactId, true, ct), r => Ok(r.Contact));

    private IActionResult Respond(CustomerResult result, Func<CustomerResult, IActionResult> ok)
    {
        switch (result.Outcome)
        {
            case CustomerOutcome.Ok:
                return ok(result);

            case CustomerOutcome.NotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
                    detail: "There is no such customer or contact.");

            case CustomerOutcome.AccountCodeTaken:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "Account code already in use",
                    detail: result.Problem);

            case CustomerOutcome.Invalid:
                foreach (var (field, messages) in result.Errors!)
                    foreach (var message in messages)
                        ModelState.AddModelError(field, message);
                return ValidationProblem(ModelState);

            default:
                return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

/// <summary>The body of POST and PUT /api/customers. Lengths match the database columns.</summary>
public sealed class CustomerRequest
{
    [Required, StringLength(200)]
    public string Name { get; set; } = "";

    /// <summary>The Sage Pastel account code, for example RAW001.</summary>
    [StringLength(40)]
    public string? AccountCode { get; set; }

    [StringLength(200)]
    public string? AddressLine1 { get; set; }

    [StringLength(200)]
    public string? AddressLine2 { get; set; }

    [StringLength(100)]
    public string? City { get; set; }

    [StringLength(20)]
    public string? PostalCode { get; set; }

    [StringLength(40)]
    public string? VatNumber { get; set; }

    public CustomerInput ToInput() =>
        new(Name, AccountCode, AddressLine1, AddressLine2, City, PostalCode, VatNumber);
}

/// <summary>The body of POST and PUT /api/customers/{id}/contacts.</summary>
public sealed class ContactRequest
{
    [Required, StringLength(200)]
    public string FullName { get; set; } = "";

    [EmailAddress, StringLength(200)]
    public string? Email { get; set; }

    [StringLength(40)]
    public string? Phone { get; set; }

    [StringLength(100)]
    public string? Position { get; set; }

    public ContactInput ToInput() => new(FullName, Email, Phone, Position);
}
