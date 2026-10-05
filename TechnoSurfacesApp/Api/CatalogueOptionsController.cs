using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Catalogue;

namespace TechnoSurfacesApp.Api;

/// <summary>
/// The cascading material choice on the costing sheet (US-01, US-02): supplier, then
/// product line, then colour, then sheet size. Read-only. Every signed-in user may
/// call it, because estimators see all pricing by client decision.
/// </summary>
[ApiController]
[Route("api/catalogue")]
public sealed class CatalogueOptionsController : ControllerBase
{
    private readonly ICatalogueBrowser _catalogue;

    public CatalogueOptionsController(ICatalogueBrowser catalogue) => _catalogue = catalogue;

    /// <summary>GET /api/catalogue/suppliers</summary>
    [HttpGet("suppliers")]
    [ProducesResponseType<IReadOnlyList<SupplierOption>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Suppliers(CancellationToken ct) =>
        Ok(await _catalogue.SuppliersAsync(ct));

    /// <summary>GET /api/catalogue/suppliers/{id}/product-lines</summary>
    [HttpGet("suppliers/{id:int}/product-lines")]
    [ProducesResponseType<IReadOnlyList<ProductLineOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ProductLines(int id, CancellationToken ct) =>
        Found(await _catalogue.ProductLinesAsync(id, ct), "supplier", id);

    /// <summary>GET /api/catalogue/product-lines/{id}/colours</summary>
    [HttpGet("product-lines/{id:int}/colours")]
    [ProducesResponseType<IReadOnlyList<ColourOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Colours(int id, CancellationToken ct) =>
        Found(await _catalogue.ColoursAsync(id, ct), "product line", id);

    /// <summary>GET /api/catalogue/colours/{id}/sheet-sizes</summary>
    [HttpGet("colours/{id:int}/sheet-sizes")]
    [ProducesResponseType<IReadOnlyList<SheetSizeOption>>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SheetSizes(int id, CancellationToken ct) =>
        Found(await _catalogue.SheetSizesAsync(id, ct), "colour", id);

    /// <summary>GET /api/catalogue/rate-items: the rate card lines that can be added to a quote.</summary>
    [HttpGet("rate-items")]
    [ProducesResponseType<IReadOnlyList<RateItemOption>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> RateItems(CancellationToken ct) =>
        Ok(await _catalogue.RateItemsAsync(ct));

    private IActionResult Found<T>(IReadOnlyList<T>? options, string parent, int id) =>
        options is null
            ? Problem(statusCode: StatusCodes.Status404NotFound, title: $"No such {parent}",
                detail: $"There is no {parent} {id} in the catalogue.")
            : Ok(options);
}
