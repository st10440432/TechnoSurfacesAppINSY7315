using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TechnoSurfacesApp.Models;
using TechnoSurfacesApp.Services;

namespace TechnoSurfacesApp.Controllers;

/// <summary>
/// Authentication screens. There is deliberately no registration action: all
/// accounts are created by the Managing Director (US-26). The sign-in rules live
/// in ISignInService; this controller only validates input and chooses the page.
/// </summary>
public class AccountController : Controller
{
    private readonly ISignInService _signIn;

    public AccountController(ISignInService signIn) => _signIn = signIn;

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Login(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
            return RedirectToAction("Dashboard", "Home");

        return View(new LoginViewModel { ReturnUrl = returnUrl });
    }

    [AllowAnonymous]
    [HttpPost]
    public async Task<IActionResult> Login(LoginViewModel model)
    {
        if (!ModelState.IsValid)
            return View(model);

        var outcome = await _signIn.SignInAsync(model.Email, model.Password, model.RememberMe);

        if (outcome == SignInOutcome.Succeeded)
        {
            // Only return to a page on this site - blocks open-redirect attacks.
            return Url.IsLocalUrl(model.ReturnUrl)
                ? LocalRedirect(model.ReturnUrl!)
                : RedirectToAction("Dashboard", "Home");
        }

        // One message for an unknown email and a wrong password, so the form
        // cannot be used to discover which accounts exist.
        ModelState.AddModelError(string.Empty, outcome switch
        {
            SignInOutcome.LockedOut =>
                "Too many failed attempts. This account is temporarily locked - try again later or ask the Managing Director.",
            SignInOutcome.Deactivated =>
                "This account has been deactivated. Ask the Managing Director if you need access.",
            _ =>
                "The email address or password is incorrect."
        });

        model.Password = string.Empty;
        return View(model);
    }

    /// <summary>POST only, so another site cannot sign a user out with a link or image tag.</summary>
    [HttpPost]
    public async Task<IActionResult> Logout()
    {
        await _signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    /// <summary>Shown after signing in with a temporary password (Task 1 8.2).</summary>
    [HttpGet]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [HttpPost]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid)
            return View(new ChangePasswordViewModel());

        var errors = await _signIn.ChangePasswordAsync(User, model.CurrentPassword, model.NewPassword);
        if (errors.Count == 0)
            return RedirectToAction("Dashboard", "Home");

        foreach (var error in errors)
            ModelState.AddModelError(string.Empty, error);

        // Passwords are never written back into the page.
        return View(new ChangePasswordViewModel());
    }

    /// <summary>
    /// Shown instead of a 403 error when a signed-in user opens a page their role
    /// cannot use (Task 1 2.4). It carries no data from the page they asked for.
    /// </summary>
    [HttpGet]
    public IActionResult AccessDenied() => View();

    // Forgotten password and account activation (Task 1 8.2). There is no email
    // service, so neither sends a link: the Managing Director issues a temporary
    // password and the user replaces it at the first sign-in. Both pages explain
    // the steps and accept no input, so there is nothing to post.

    [AllowAnonymous]
    [HttpGet]
    public IActionResult ForgotPassword() => View();

    [AllowAnonymous]
    [HttpGet]
    public IActionResult Activate() => View();
}