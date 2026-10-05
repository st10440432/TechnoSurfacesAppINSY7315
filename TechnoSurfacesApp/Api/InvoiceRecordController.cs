using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// The Sage Pastel invoice recorded against an accepted quote (US-25). Recorded,
/// not produced: Pastel remains the book of record. Recording needs CanRecordInvoice
/// (the Managing Director); reading is open to every signed-in user.
/// </summary>
[ApiController]
[Route("api/quotes/{quoteId:int}/invoice")]
public sealed class InvoiceRecordController : ControllerBase
{
    private readonly IInvoiceRecordService _invoices;
    private readonly IAuthorizationService _authorization;

    public InvoiceRecordController(IInvoiceRecordService invoices, IAuthorizationService authorization)
    {
        _invoices = invoices;
        _authorization = authorization;
    }

    /// <summary>GET /api/quotes/{quoteId}/invoice</summary>
    [HttpGet(Name = nameof(GetInvoice))]
    [ProducesResponseType<InvoiceRecordView>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetInvoice(int quoteId, CancellationToken ct) =>
        Respond(await _invoices.GetAsync(quoteId, ct), quoteId, created: false);

    /// <summary>POST /api/quotes/{quoteId}/invoice</summary>
    [HttpPost]
    [ProducesResponseType<InvoiceRecordView>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Record(int quoteId, RecordInvoiceRequest request, CancellationToken ct)
    {
        if (!(await _authorization.AuthorizeAsync(User, Policies.CanRecordInvoice)).Succeeded)
            return Problem(statusCode: StatusCodes.Status403Forbidden, title: "You cannot do this",
                detail: "Only the Managing Director records the Pastel invoice.");

        var result = await _invoices.RecordAsync(quoteId,
            new InvoiceInput(request.InvoiceNumber, request.InvoiceDate!.Value, request.AmountIncVat!.Value), ct);
        return Respond(result, quoteId, created: true);
    }

    private IActionResult Respond(InvoiceResult result, int quoteId, bool created)
    {
        switch (result.Outcome)
        {
            case InvoiceOutcome.Ok:
                return created
                    ? CreatedAtRoute(nameof(GetInvoice), new { quoteId }, result.Invoice)
                    : Ok(result.Invoice);

            case InvoiceOutcome.QuoteNotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Quote not found",
                    detail: $"There is no quote {quoteId}.");

            case InvoiceOutcome.NotRecorded:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "No invoice recorded",
                    detail: "No Pastel invoice has been recorded against this quote yet.");

            case InvoiceOutcome.Conflict:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "The invoice cannot be recorded",
                    detail: result.Problem);

            case InvoiceOutcome.Invalid:
                foreach (var (field, messages) in result.Errors!)
                    foreach (var message in messages)
                        ModelState.AddModelError(field, message);
                return ValidationProblem(ModelState);

            default:
                return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

/// <summary>The body of POST /api/quotes/{quoteId}/invoice, as printed on the Pastel invoice.</summary>
public sealed class RecordInvoiceRequest
{
    [Required, StringLength(InvoiceRecordService.InvoiceNumberMaxLength)]
    public string InvoiceNumber { get; set; } = "";

    [Required]
    public DateOnly? InvoiceDate { get; set; }

    [Required, Range(0.01, 9_999_999_999_999_999.99)]
    public decimal? AmountIncVat { get; set; }
}
