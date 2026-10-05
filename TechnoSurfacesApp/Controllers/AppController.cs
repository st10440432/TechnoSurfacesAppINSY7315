using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfacesApp.Models;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Shared base for the signed-in screens. Supplies the page title, the side menu
/// highlight and the breadcrumb, and the policy checks the screens use for their
/// read-only flags so the rule lives in the policy and nowhere else.
/// </summary>
public abstract class AppController : Controller
{
    /// <summary>
    /// Sets the page title, which side menu item is current, and the breadcrumb.
    /// The title becomes the page's one h1 and its browser tab name.
    /// </summary>
    protected void SetPage(string title, string navKey, params Crumb[] trail)
    {
        ViewData["Title"] = title;
        ViewData["Page"] = navKey;
        ViewData["Crumbs"] = trail.Append(new Crumb(title)).ToList();
    }

    /// <summary>Whether the signed-in user satisfies a named policy.</summary>
    protected async Task<bool> CanAsync(string policy)
    {
        var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(User, policy)).Succeeded;
    }

    /// <summary>Whether the signed-in user satisfies a resource-based policy, such as editing one quote.</summary>
    protected async Task<bool> CanAsync(object resource, string policy)
    {
        var authorization = HttpContext.RequestServices.GetRequiredService<IAuthorizationService>();
        return (await authorization.AuthorizeAsync(User, resource, policy)).Succeeded;
    }

    /// <summary>A message shown as a toast on the next page, after a redirect.</summary>
    protected void Flash(string kind, string title, string? message = null)
    {
        TempData["FlashKind"] = kind;
        TempData["FlashTitle"] = title;
        TempData["FlashMessage"] = message ?? "";
    }

    /// <summary>Today in South Africa, where the client works.</summary>
    protected static DateOnly Today =>
        TechnoSurfaces.Application.Quoting.BusinessDate.Today(TimeProvider.System);
}
