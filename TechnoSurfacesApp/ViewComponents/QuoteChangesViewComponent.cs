using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Auditing;

namespace TechnoSurfacesApp.ViewComponents;

/// <summary>
/// US-19: "Changes to this quote". The Managing Director corrects an estimator's quote
/// and approves it without returning it, so this panel is how the estimator sees what
/// was changed, by whom and when. Shown on the costing sheet (the estimator's screen)
/// and on the MD's review screen. No logic here: it asks the audit service and renders.
/// </summary>
public sealed class QuoteChangesViewComponent : ViewComponent
{
    private readonly IAuditTrailService _audit;

    public QuoteChangesViewComponent(IAuditTrailService audit) => _audit = audit;

    public async Task<IViewComponentResult> InvokeAsync(string reference) =>
        View(await _audit.ForQuoteReferenceAsync(reference, HttpContext.RequestAborted));
}