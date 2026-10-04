using Microsoft.AspNetCore.Mvc;
using TechnoSurfacesApp.Data;
using TechnoSurfacesApp.Models;
using TechnoSurfaces.Services;
using static System.Collections.Specialized.BitVector32;
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

    public AdminController(DemoSession session, IUserAdminService users, IAuditTrailService audit) : base(session)
    {
        _users = users;
        _audit = audit;
    }

    // ======================================================================
    //  Rate card
    // ======================================================================

    public async Task<IActionResult> Rates()
    {
        ViewData["Title"] = "Rate card";
        ViewData["Page"] = "rates";
        ViewData["Crumb"] = "Administration";

        return View(new RatesVm
        {
            CanEdit = await CanAsync(Policies.CanEditCatalogue),
            Groups = RateGroupOrder
                .Select(g => (g, Db.RatesIn(g)))
                .Where(x => x.Item2.Count > 0)
                .ToList()
        });
    }

    public static readonly RateGroup[] RateGroupOrder =
    {
        RateGroup.Fabrication, RateGroup.Consumables, RateGroup.Installation,
        RateGroup.WoodSubstrate, RateGroup.SinksHardware, RateGroup.BelowTheLine
    };

    // ======================================================================
    //  Users
    // ======================================================================


    [Authorize(Policy = Policies.CanManageUsers)]
    public async Task<IActionResult> Users()
    {
        ViewData["Title"] = "Users";
        ViewData["Page"] = "users";
        ViewData["Crumb"] = "Administration";

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
        ViewData["Title"] = "Quotation terms";
        ViewData["Page"] = "terms";
        ViewData["Crumb"] = "Administration";

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
        ViewData["Title"] = "Audit trail";
        ViewData["Page"] = "audit";
        ViewData["Crumb"] = "Administration";

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
    public List<(RateGroup Group, List<RateItem> Rows)> Groups { get; set; } = new();

    public string GroupName(RateGroup g) => g switch
    {
        RateGroup.Fabrication => "Fabrication",
        RateGroup.Consumables => "Consumables",
        RateGroup.Installation => "Installation",
        RateGroup.WoodSubstrate => "Wood & substrate",
        RateGroup.SinksHardware => "Sinks & hardware",
        _ => "Below the line \u2014 cost recovery, not marked up"
    };
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