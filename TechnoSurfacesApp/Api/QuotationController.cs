using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// The customer quotation (US-10 to US-14) and the customer-facing lines it is built
/// from, organised by room or element.
///
/// GET quotation returns the document with no cost, discount or markup field on it
/// (US-11, NFR-02). GET quotation/check is internal: it compares the quotation with
/// the costing total so the estimator can make them match (US-10). Writing a line
/// needs CanEditQuote, like the costing sheet.
/// </summary>
[ApiController]
[Route("api/quotes/{quoteId:int}")]
public sealed class QuotationController : ControllerBase
{
    private readonly IQuotationGenerationService _quotation;
    private readonly IQuoteRepository _quotes;
    private readonly IAuthorizationService _authorization;

    public QuotationController(IQuotationGenerationService quotation, IQuoteRepository quotes, IAuthorizationService authorization)
    {
        _quotation = quotation;
        _quotes = quotes;
        _authorization = authorization;
    }

    /// <summary>GET /api/quotes/{quoteId}/quotation: the customer document.</summary>
    [HttpGet("quotation")]
    [ProducesResponseType<CustomerQuotation>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Quotation(int quoteId, CancellationToken ct) =>
        await _quotation.GenerateAsync(quoteId, ct) is { } document ? Ok(document) : QuoteNotFound(quoteId);

    /// <summary>
    /// GET /api/quotes/{quoteId}/versions/{versionNo}/quotation: the customer document
    /// for any version, as it was issued (US-21).
    /// </summary>
    [HttpGet("versions/{versionNo:int}/quotation")]
    [ProducesResponseType<CustomerQuotation>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VersionQuotation(int quoteId, int versionNo, CancellationToken ct) =>
        await _quotation.GenerateVersionAsync(quoteId, versionNo, ct) is { } document
            ? Ok(document)
            : Problem(statusCode: StatusCodes.Status404NotFound, title: "Version not found",
                detail: $"Quote {quoteId} has no version {versionNo}.");

    /// <summary>GET /api/quotes/{quoteId}/quotation/check: internal, quotation total against costing total.</summary>
    [HttpGet("quotation/check")]
    [ProducesResponseType<QuotationCheck>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Check(int quoteId, CancellationToken ct) =>
        await _quotation.CheckAsync(quoteId, ct) is { } check ? Ok(check) : QuoteNotFound(quoteId);

    /// <summary>POST /api/quotes/{quoteId}/quotation-lines</summary>
    [HttpPost("quotation-lines")]
    [ProducesResponseType<QuotationResult>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddLine(int quoteId, QuotationLineRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _quotation.AddLineAsync(quoteId, request.ToInput(), ct);
        return result.Outcome == QuotationOutcome.Ok
            ? Created($"/api/quotes/{quoteId}/quotation", result)
            : Failure(result, quoteId);
    }

    /// <summary>PUT /api/quotes/{quoteId}/quotation-lines/{lineId}</summary>
    [HttpPut("quotation-lines/{lineId:int}")]
    [ProducesResponseType<QuotationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeLine(int quoteId, int lineId, QuotationLineRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _quotation.ChangeLineAsync(quoteId, lineId, request.ToInput(), ct);
        return result.Outcome == QuotationOutcome.Ok ? Ok(result) : Failure(result, quoteId);
    }

    /// <summary>DELETE /api/quotes/{quoteId}/quotation-lines/{lineId}</summary>
    [HttpDelete("quotation-lines/{lineId:int}")]
    [ProducesResponseType<QuotationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveLine(int quoteId, int lineId, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _quotation.RemoveLineAsync(quoteId, lineId, ct);
        return result.Outcome == QuotationOutcome.Ok ? Ok(result) : Failure(result, quoteId);
    }

    /// <summary>PUT /api/quotes/{quoteId}/quotation-lines/order: every line id, in the new order.</summary>
    [HttpPut("quotation-lines/order")]
    [ProducesResponseType<QuotationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Reorder(int quoteId, QuotationOrderRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _quotation.ReorderAsync(quoteId, request.LineIds, ct);
        return result.Outcome == QuotationOutcome.Ok ? Ok(result) : Failure(result, quoteId);
    }

    /// <summary>
    /// POST /api/quotes/{quoteId}/quotation-lines/from-costing: one line per material
    /// at its selling price, then one line for everything else, added after any lines
    /// already written.
    /// </summary>
    [HttpPost("quotation-lines/from-costing")]
    [ProducesResponseType<QuotationResult>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddLinesFromCosting(int quoteId, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _quotation.AddLinesFromCostingAsync(quoteId, ct);
        return result.Outcome == QuotationOutcome.Ok ? Ok(result) : Failure(result, quoteId);
    }

    private async Task<IActionResult?> RefuseUnlessEditableAsync(int quoteId, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return QuoteNotFound(quoteId);

        var allowed = await _authorization.AuthorizeAsync(User, quote, Policies.CanEditQuote);
        return allowed.Succeeded
            ? null
            : Problem(statusCode: StatusCodes.Status403Forbidden, title: "You cannot change this quote",
                detail: "The Managing Director can change any quote. An estimator can change only their own quote while it is a draft.");
    }

    private ObjectResult QuoteNotFound(int quoteId) =>
        Problem(statusCode: StatusCodes.Status404NotFound, title: "Quote not found", detail: $"There is no quote {quoteId}.");

    private IActionResult Failure(QuotationResult result, int quoteId)
    {
        switch (result.Outcome)
        {
            case QuotationOutcome.QuoteNotFound:
                return QuoteNotFound(quoteId);

            case QuotationOutcome.LineNotFound:
                return Problem(statusCode: StatusCodes.Status404NotFound, title: "Line not found",
                    detail: $"Quote {quoteId} has no such quotation line on its current version.");

            case QuotationOutcome.VersionSealed:
                return Problem(statusCode: StatusCodes.Status409Conflict, title: "This version can no longer be changed",
                    detail: result.Problem);

            case QuotationOutcome.Invalid:
                foreach (var (field, messages) in result.Errors!)
                    foreach (var message in messages)
                        ModelState.AddModelError(field, message);
                return ValidationProblem(ModelState);

            default:
                return Problem(statusCode: StatusCodes.Status500InternalServerError);
        }
    }
}

/// <summary>The body of POST and PUT quotation-lines. Lengths match the database columns.</summary>
public sealed class QuotationLineRequest
{
    [Required, StringLength(QuotationGenerationService.DescriptionMaxLength)]
    public string Description { get; set; } = "";

    /// <summary>The room or element, as the customer would describe it.</summary>
    [StringLength(QuotationGenerationService.RoomMaxLength)]
    public string? Room { get; set; }

    [Range(0, 99_999_999_999_999.9999)]
    public decimal Quantity { get; set; } = 1m;

    /// <summary>What the customer is charged for this line, excluding VAT.</summary>
    [Range(0, 9_999_999_999_999_999.99)]
    public decimal AmountExVat { get; set; }

    public QuotationLineInput ToInput() => new(Description, AmountExVat, Room, Quantity);
}

/// <summary>The body of PUT quotation-lines/order.</summary>
public sealed class QuotationOrderRequest
{
    [Required]
    public List<int> LineIds { get; set; } = new();
}
