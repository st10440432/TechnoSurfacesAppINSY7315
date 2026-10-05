using Microsoft.AspNetCore.Mvc;
using TechnoSurfacesApp.Models;
using Microsoft.AspNetCore.Authorization;
using TechnoSurfacesApp.Identity;
using System.Security.Claims;
using TechnoSurfacesApp.Services;
using System.Globalization;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Catalogue;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Managing Director only. Price changes live in CatalogueController; this
/// controller covers the rate card, user accounts, quotation boilerplate and
/// the audit trail.
/// </summary>
public class AdminController : AppController
{
    private readonly IUserAdminService _users;
    private readonly IAuditTrailService _audit;

    public AdminController(IUserAdminService users, IAuditTrailService audit)
    {
        _users = users;
        _audit = audit;
    }

    // ======================================================================
    //  Rate card
    // ======================================================================

    public async Task<IActionResult> Rates([FromServices] ICatalogueService catalogue, CancellationToken ct)
    {
        SetPage("Rate card", "rates", new Crumb("Administration"));

        var rows = await catalogue.GetRateCardAsync(Today, ct);
        return View(new RatesVm
        {
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Today = Today,
            Groups = Enum.GetNames<TechnoSurfaces.Domain.RateCategory>()
                .Select(c => (c, rows.Where(r => r.Category == c && !r.IsRetired).OrderBy(r => r.Name).ThenBy(r => r.Supplier).ToList()))
                .Where(g => g.Item2.Count > 0)
                .ToList()
        });
    }

    // ======================================================================
    //  Users
    // ======================================================================


    [Authorize(Policy = Policies.CanManageUsers)]
    public async Task<IActionResult> Users()
    {
        SetPage("Users", "users", new Crumb("Administration"));

        return View(new UsersVm
        {
            Users = await _users.ListAsync(),
            CanManage = true,
            MyId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? ""
        });
    }

    [HttpPost]
    [Authorize(Policy = Policies.CanManageUsers)]
    public async Task<IActionResult> CreateUser(CreateUserForm form) =>
        UsersOutcome(ModelState.IsValid
                ? await _users.CreateAsync(form.FullName, form.Email, form.Role)
                : UserAdminResult.Fail("Enter a name, a valid email address and a role."),
            $"Account created for {form.Email.Trim()}.");

    [HttpPost]
    [Authorize(Policy = Policies.CanManageUsers)]
    public async Task<IActionResult> SetUserActive(string userId, bool active) =>
        UsersOutcome(await _users.SetActiveAsync(userId, active),
            active ? "Account reactivated." : "Account deactivated. Any open session ends within a minute.");

    [HttpPost]
    [Authorize(Policy = Policies.CanManageUsers)]
    public async Task<IActionResult> ReissuePassword(string userId) =>
        UsersOutcome(await _users.ReissuePasswordAsync(userId), "New temporary password issued.");

    private IActionResult UsersOutcome(UserAdminResult result, string success)
    {
        if (!result.Succeeded)
        {
            TempData["UserError"] = result.Error;
        }
        else
        {
            TempData["UserMessage"] = success;
            if (result.TemporaryPassword is not null)
                TempData["TemporaryPassword"] = result.TemporaryPassword;
        }

        return RedirectToAction(nameof(Users));
    }

    // ======================================================================
    //  Quotation terms - the standing content on every customer quotation
    // ======================================================================

    public async Task<IActionResult> Terms([FromServices] ICatalogueService catalogue, CancellationToken ct)
    {
        SetPage("Quotation terms", "terms", new Crumb("Administration"));

        return View(new TermsVm
        {
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Terms = await catalogue.GetTermsAsync(ct),
            Brands = await catalogue.GetBrandsAsync(ct)
        });
    }

    // ======================================================================
    //  Audit trail
    // ======================================================================

    [Authorize(Policy = Policies.CanViewAuditTrail)]
    public async Task<IActionResult> Audit(string? user, string? type, DateOnly? from, DateOnly? to, bool priceOnly, CancellationToken ct)
    {
        SetPage("Audit trail", "audit", new Crumb("Administration"));

        var filter = new AuditFilter(user, type, from, to, priceOnly);
        var page = await _audit.SearchAsync(filter, ct);

        return View(new AuditVm
        {
            Filter = filter,
            Entries = page.Rows,
            TotalMatching = page.TotalMatching,
            Truncated = page.Truncated,
            EntityTypes = await _audit.EntityNamesAsync(ct),
            Users = await _audit.UsersAsync(ct)
        });
    }
}

// ==========================================================================
//  View models
// ==========================================================================

public class RatesVm
{
    public bool CanEdit { get; set; }
    public DateOnly Today { get; set; }
    public List<(string Category, List<RateCardRow> Rows)> Groups { get; set; } = new();

    /// <summary>The rate a derived item works out to: its base rate times the multiplier.</summary>
    public decimal? DerivedAmount(RateCardRow row)
    {
        if (row.DerivedFrom is null) return null;
        var source = Groups.SelectMany(g => g.Rows)
            .FirstOrDefault(r => r.Name == row.DerivedFrom && r.SupplierId == row.SupplierId && r.Amount is not null);
        // Rounded as RateResolver rounds it, so the screen shows the figure a quote is charged.
        return source?.Amount is { } amount ? decimal.Round(amount * (row.Multiplier ?? 1m), 2, MidpointRounding.AwayFromZero) : null;
    }
}

public class UsersVm
{
    public IReadOnlyList<UserRow> Users { get; set; } = Array.Empty<UserRow>();
    public bool CanManage { get; set; }
    public string MyId { get; set; } = "";

    public int ActiveCount => Users.Count(u => u.IsActive);
    public int MdCount => Users.Count(u => u.Role == UserRole.ManagingDirector);
    public int EstimatorCount => Users.Count(u => u.Role == UserRole.Estimator && u.IsActive);
}

public class TermsVm
{
    public bool CanEdit { get; set; }
    public IReadOnlyList<TermRow> Terms { get; set; } = [];
    public IReadOnlyList<BrandRow> Brands { get; set; } = [];

    public List<TermRow> ActiveIn(TechnoSurfaces.Domain.TermSection section) =>
        Terms.Where(t => t.Section == section && t.IsActive).ToList();

    public List<TermRow> RetiredIn(TechnoSurfaces.Domain.TermSection section) =>
        Terms.Where(t => t.Section == section && !t.IsActive).ToList();
}

public class AuditVm
{
    public AuditFilter Filter { get; init; } = new();
    public IReadOnlyList<AuditRow> Entries { get; init; } = [];
    public int TotalMatching { get; init; }
    public bool Truncated { get; init; }
    public IReadOnlyList<string> EntityTypes { get; init; } = [];
    public IReadOnlyList<AuditUserOption> Users { get; init; } = [];

    public bool AnyFilter => !Filter.IsEmpty;
    public int PriceChangeCount => Entries.Count(e => e.IsPriceChange);
    public string? FromValue => Filter.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string? ToValue => Filter.To?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}