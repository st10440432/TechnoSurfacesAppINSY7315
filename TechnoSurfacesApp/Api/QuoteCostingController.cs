using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// The costing sheet of a quote: its items, each with its own priced lines and
/// markup, the transport on the open version, and the totals. Called by the costing
/// sheet screen, which works on one item at a time. A quote has at least one item;
/// the routes that name no item work on the first, so a one-item quote is used
/// exactly as before items.
///
/// Every error is a ProblemDetails body with the matching status code, never 200
/// with a failure flag. A price that cannot be resolved is 422 with the reason, and
/// no line is created (NFR-01, US-03). Write calls need the antiforgery token in a
/// RequestVerificationToken header.
///
/// Every write is checked against the CanEditQuote policy first: the Managing
/// Director may change any quote, an estimator only their own draft. Reading is
/// open to every signed-in user, because estimators see all pricing by client
/// decision.
/// </summary>
[ApiController]
[Route("api/quotes/{quoteId:int}")]
public sealed class QuoteCostingController : ControllerBase
{
    private readonly ICostingSheetService _costing;
    private readonly IQuoteRepository _quotes;
    private readonly IAuthorizationService _authorization;

    public QuoteCostingController(ICostingSheetService costing, IQuoteRepository quotes, IAuthorizationService authorization)
    {
        _costing = costing;
        _quotes = quotes;
        _authorization = authorization;
    }

    /// <summary>GET /api/quotes/{quoteId}/costing: the current version, its lines and totals.</summary>
    [HttpGet("costing")]
    [ProducesResponseType<CostingSheetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Costing(int quoteId, CancellationToken ct)
    {
        var result = await _costing.GetAsync(quoteId, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(CostingSheetDto.From(result.Quote!, result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// GET /api/quotes/{quoteId}/versions/{versionNo}/costing: any version of the quote,
    /// read only, as it was issued (US-21).
    /// </summary>
    [HttpGet("versions/{versionNo:int}/costing")]
    [ProducesResponseType<CostingSheetDto>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> VersionCosting(int quoteId, int versionNo, CancellationToken ct)
    {
        var result = await _costing.GetVersionAsync(quoteId, versionNo, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(CostingSheetDto.From(result.Quote!, result.Version!, result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// PUT /api/quotes/{quoteId}/costing: markup (US-07) for every item, the
    /// transport amount and the allowance. PUT /api/quotes/{quoteId}/items/{itemId}/costing: one item's markup.
    /// </summary>
    [HttpPut("costing")]
    [HttpPut("items/{itemId:int}/costing")]
    [ProducesResponseType<QuoteTotals>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeCosting(int quoteId, int? itemId, ChangeCostingRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.ChangeCostingAsync(quoteId,
            new ChangeCosting(request.MarkupPercent, request.TransportAmount, itemId, request.AllowanceAmount), ct);
        return result.Outcome == CostingOutcome.Ok ? Ok(result.Totals) : Failure(result, quoteId);
    }

    /// <summary>GET /api/quotes/{quoteId}/totals: a full recalculation (US-09).</summary>
    [HttpGet("totals")]
    [ProducesResponseType<QuoteTotals>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Totals(int quoteId, CancellationToken ct)
    {
        var result = await _costing.GetAsync(quoteId, ct);
        return result.Outcome == CostingOutcome.Ok ? Ok(result.Totals) : Failure(result, quoteId);
    }

    /// <summary>GET /api/quotes/{quoteId}/lines/{lineId}</summary>
    [HttpGet("lines/{lineId:int}", Name = nameof(GetLine))]
    [ProducesResponseType<LineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLine(int quoteId, int lineId, CancellationToken ct)
    {
        var result = await _costing.GetLineAsync(quoteId, lineId, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(new LineResponse(CostingLineDto.From(result.Line!), result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// POST /api/quotes/{quoteId}/items/{itemId}/lines (US-01, US-03, US-04): a line on
    /// one item's costing. POST /api/quotes/{quoteId}/lines adds it to the first item.
    /// </summary>
    [HttpPost("lines")]
    [HttpPost("items/{itemId:int}/lines")]
    [ProducesResponseType<LineResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddLine(int quoteId, int? itemId, AddLineRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = request.Type == AddLineRequest.Material
            ? await _costing.AddMaterialLineAsync(quoteId,
                new AddMaterialLine(request.ColourId!.Value, request.SheetSizeId!.Value, request.Quantity, request.SupplierDiscountPercent, itemId), ct)
            : await _costing.AddRateLineAsync(quoteId,
                new AddRateLine(request.RateItemId!.Value, request.Quantity, request.UnitPrice, itemId), ct);

        if (result.Outcome != CostingOutcome.Ok)
            return Failure(result, quoteId);

        return CreatedAtRoute(nameof(GetLine), new { quoteId, lineId = result.Line!.Id },
            new LineResponse(CostingLineDto.From(result.Line), result.Totals!));
    }

    /// <summary>PUT /api/quotes/{quoteId}/lines/{lineId}: quantity, rate or discount on this quote only (US-04, US-06, US-08).</summary>
    [HttpPut("lines/{lineId:int}")]
    [ProducesResponseType<LineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> ChangeLine(int quoteId, int lineId, ChangeLineRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.ChangeLineAsync(quoteId, lineId, new ChangeLine(
            request.Quantity, request.UnitPrice, request.ClearPriceOverride,
            request.SupplierDiscountPercent, request.RestoreDerivedQuantity), ct);

        return result.Outcome == CostingOutcome.Ok
            ? Ok(new LineResponse(CostingLineDto.From(result.Line!), result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// DELETE /api/quotes/{quoteId}/lines/{lineId}. Answers with the new totals, as
    /// adding and changing a line do, so the costing sheet does not ask for them
    /// separately.
    /// </summary>
    [HttpDelete("lines/{lineId:int}")]
    [ProducesResponseType<RemovedLineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveLine(int quoteId, int lineId, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.RemoveLineAsync(quoteId, lineId, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(new RemovedLineResponse(result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>POST /api/quotes/{quoteId}/items: a new item with an empty costing sheet.</summary>
    [HttpPost("items")]
    [ProducesResponseType<ItemResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddItem(int quoteId, ItemRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.AddItemAsync(quoteId, request.ToInput(), ct);
        return result.Outcome == CostingOutcome.Ok
            ? Created($"/api/quotes/{quoteId}/costing", new ItemResponse(ItemDto.From(result.Item!), result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>PUT /api/quotes/{quoteId}/items/{itemId}: its name, number, quantity and markup.</summary>
    [HttpPut("items/{itemId:int}")]
    [ProducesResponseType<ItemResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ChangeItem(int quoteId, int itemId, ItemRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.ChangeItemAsync(quoteId, itemId, request.ToInput(), ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(new ItemResponse(ItemDto.From(result.Item!), result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>DELETE /api/quotes/{quoteId}/items/{itemId}: the item and its costing. The last item stays.</summary>
    [HttpDelete("items/{itemId:int}")]
    [ProducesResponseType<RemovedLineResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RemoveItem(int quoteId, int itemId, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.RemoveItemAsync(quoteId, itemId, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(new RemovedLineResponse(result.Totals!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// GET /api/quotes/{quoteId}/sheet-summary: internal, the sheets and seam kits per
    /// item and material, the order with its buffer stock, and what it costs to buy.
    /// </summary>
    [HttpGet("sheet-summary")]
    [ProducesResponseType<SheetSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SheetSummary(int quoteId, CancellationToken ct) =>
        await _costing.GetSheetSummaryAsync(quoteId, ct: ct) is { } summary
            ? Ok(summary)
            : Failure(new CostingResult(CostingOutcome.QuoteNotFound), quoteId);

    /// <summary>PUT /api/quotes/{quoteId}/buffer-stock: one material's buffer stock; no sheets takes it off.</summary>
    [HttpPut("buffer-stock")]
    [ProducesResponseType<SheetSummary>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> BufferStock(int quoteId, BufferStockRequest request, CancellationToken ct)
    {
        if (await RefuseUnlessEditableAsync(quoteId, ct) is { } refused)
            return refused;

        var result = await _costing.SetBufferStockAsync(quoteId, request.Material, request.Sheets, ct);
        return result.Outcome == CostingOutcome.Ok
            ? Ok(SheetsAndAdhesive.Summarise(result.Version!))
            : Failure(result, quoteId);
    }

    /// <summary>
    /// The resource-based CanEditQuote check, which needs the quote itself. Returns
    /// the response to send when the quote is missing or the user may not change
    /// it, and null when the write may go ahead.
    /// </summary>
    private async Task<IActionResult?> RefuseUnlessEditableAsync(int quoteId, CancellationToken ct)
    {
        var quote = await _quotes.GetAsync(quoteId, ct);
        if (quote is null)
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Quote not found",
                detail: $"There is no quote {quoteId}.");

        var allowed = await _authorization.AuthorizeAsync(User, quote, Policies.CanEditQuote);
        return allowed.Succeeded
            ? null
            : Problem(statusCode: StatusCodes.Status403Forbidden, title: "You cannot change this quote",
                detail: "The Managing Director can change any quote. An estimator can change only their own quote while it is a draft.");
    }

    private IActionResult Failure(CostingResult result, int quoteId) => result.Outcome switch
    {
        CostingOutcome.QuoteNotFound => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Quote not found",
            detail: $"There is no quote {quoteId}."),

        CostingOutcome.VersionNotFound => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Version not found",
            detail: result.Problem),

        CostingOutcome.LineNotFound => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Line not found",
            detail: $"Quote {quoteId} has no such line on its current version."),

        CostingOutcome.PriceNotResolved => Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity, title: "The price could not be resolved",
            detail: result.Problem),

        CostingOutcome.NotSelectable => Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity, title: "This item cannot be quoted",
            detail: result.Problem),

        CostingOutcome.ItemNotFound => Problem(
            statusCode: StatusCodes.Status404NotFound, title: "Item not found",
            detail: result.Problem),

        CostingOutcome.NotApplicable => Problem(
            statusCode: StatusCodes.Status422UnprocessableEntity, title: "This change does not apply",
            detail: result.Problem),

        CostingOutcome.VersionSealed => Problem(
            statusCode: StatusCodes.Status409Conflict, title: "This version can no longer be changed",
            detail: result.Problem),

        _ => Problem(statusCode: StatusCodes.Status500InternalServerError)
    };
}
