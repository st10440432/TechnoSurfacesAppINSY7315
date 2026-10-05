using Microsoft.AspNetCore.Mvc;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfacesApp.Identity;
using TechnoSurfacesApp.Services;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// The dashboard. The Managing Director lands on the approval queue, the one task
/// only the Managing Director can do; an estimator lands on their own quotes.
/// </summary>
public class HomeController : AppController
{
    private const int StaleAfterDays = 365;

    private readonly IQuoteWorkflowService _workflow;
    private readonly ICatalogueBrowser _catalogue;
    private readonly IAuditTrailService _audit;
    private readonly SignedInUser _me;

    public HomeController(IQuoteWorkflowService workflow, ICatalogueBrowser catalogue,
        IAuditTrailService audit, SignedInUser me)
    {
        _workflow = workflow;
        _catalogue = catalogue;
        _audit = audit;
        _me = me;
    }

    public IActionResult Index() => RedirectToAction(nameof(Dashboard));

    public async Task<IActionResult> Dashboard(CancellationToken ct)
    {
        var isManagingDirector = await CanAsync(Policies.CanApproveQuote);
        var all = await _workflow.ListAsync(new QuoteListFilter(), ct);
        var suppliers = await _catalogue.SuppliersAsync(ct);

        var vm = new DashboardVm
        {
            FirstName = (await _me.GetAsync()).FullName.Split(' ')[0],
            IsManagingDirector = isManagingDirector,
            AllQuotes = all,
            MyQuotes = all.Where(q => q.AuthorId == _me.Id).ToList(),
            Queue = isManagingDirector ? await _workflow.ApprovalQueueAsync(ct) : [],
            StaleSuppliers = suppliers.Where(s => s.PriceListDated.AddDays(StaleAfterDays) < Today).ToList(),
            SupplierCount = suppliers.Count,
            Activity = await CanAsync(Policies.CanViewAuditTrail)
                ? (await _audit.SearchAsync(new AuditFilter(), ct)).Rows.Take(8).ToList()
                : []
        };

        SetPage("Dashboard", "dashboard");
        return View(vm);
    }
}

public sealed class DashboardVm
{
    public string FirstName { get; init; } = "";
    public bool IsManagingDirector { get; init; }
    public IReadOnlyList<QuoteSummary> AllQuotes { get; init; } = [];
    public IReadOnlyList<QuoteSummary> MyQuotes { get; init; } = [];
    public IReadOnlyList<QuoteSummary> Queue { get; init; } = [];
    public IReadOnlyList<SupplierOption> StaleSuppliers { get; init; } = [];
    public int SupplierCount { get; init; }
    public IReadOnlyList<AuditRow> Activity { get; init; } = [];

    /// <summary>The quotes the tiles count: every quote for the MD, their own for an estimator.</summary>
    public IReadOnlyList<QuoteSummary> Scope => IsManagingDirector ? AllQuotes : MyQuotes;

    public int Count(string status) => Scope.Count(q => q.Status == status);

    public decimal Value(string status) => Scope.Where(q => q.Status == status).Sum(q => q.TotalExVat);

    public int ExpiringSoon => Scope.Count(q => q.ExpiresSoon);

    /// <summary>The table under the tiles: the most recent quotes in scope.</summary>
    public IReadOnlyList<QuoteSummary> Recent => Scope.Take(6).ToList();
}
