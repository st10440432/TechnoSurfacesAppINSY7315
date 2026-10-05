using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Quoting;

namespace TechnoSurfacesApp.ViewComponents;

/// <summary>
/// The number of quotes waiting for approval, shown on the side menu. Rendered for
/// the Managing Director only, who alone works the approval queue.
/// </summary>
public sealed class ApprovalCountViewComponent : ViewComponent
{
    private readonly IQuoteWorkflowService _workflow;

    public ApprovalCountViewComponent(IQuoteWorkflowService workflow) => _workflow = workflow;

    public async Task<IViewComponentResult> InvokeAsync() =>
        View(await _workflow.PendingCountAsync(HttpContext.RequestAborted));
}
