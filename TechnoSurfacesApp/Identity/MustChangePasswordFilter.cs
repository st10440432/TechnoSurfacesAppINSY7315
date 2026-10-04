using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TechnoSurfacesApp.Identity;

/// <summary>
/// Until a temporary password has been replaced, the only pages a user can reach
/// are "Set your password" and sign-out. Applied globally, so no page - including
/// ones added later - can be reached around it.
/// </summary>
public sealed class MustChangePasswordFilter : IActionFilter
{
    public void OnActionExecuting(ActionExecutingContext context)
    {
        if (!context.HttpContext.User.HasClaim(c => c.Type == AppClaimsPrincipalFactory.MustChangePasswordClaim))
            return;

        var controller = context.RouteData.Values["controller"] as string;
        var action = context.RouteData.Values["action"] as string;

        if (controller == "Account" && (action == "ChangePassword" || action == "Logout"))
            return;

        context.Result = new RedirectToActionResult("ChangePassword", "Account", null);
    }

    public void OnActionExecuted(ActionExecutedContext context) { }
}