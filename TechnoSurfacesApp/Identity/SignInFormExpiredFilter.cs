using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Core.Infrastructure;
using Microsoft.AspNetCore.Mvc.Filters;

namespace TechnoSurfacesApp.Identity;

public sealed class SignInFormExpiredFilter : IAlwaysRunResultFilter
{
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not IAntiforgeryValidationFailedResult)
            return;

        var controller = context.RouteData.Values["controller"] as string;
        var action = context.RouteData.Values["action"] as string;
        if (!string.Equals(controller, "Account", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(action, "Login", StringComparison.OrdinalIgnoreCase))
            return;

        context.Result = context.HttpContext.User.Identity?.IsAuthenticated == true
            ? new RedirectToActionResult("Dashboard", "Home", null)
            : new RedirectToActionResult("Login", "Account", new { expired = true });
    }

    public void OnResultExecuted(ResultExecutedContext context) { }
}