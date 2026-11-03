using System.Net;
using System.Text.RegularExpressions;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.FrontEnd;

/// <summary>
/// The show or hide button beside every password field (client request). The
/// switching itself is in app.js; these check that each field has its own button,
/// that the button can never send the form, and that the field starts hidden.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class ShowPasswordTests
{
    private readonly AppFactory _app;

    public ShowPasswordTests(AppFactory app) => _app = app;

    [Fact]
    public async Task The_sign_in_password_has_a_show_button()
    {
        var html = await _app.CreateBrowser().GetStringAsync("/Account/Login");

        AssertHasToggle(html, "Password", "current-password");
    }

    [Fact]
    public async Task Every_field_on_set_your_password_has_a_show_button()
    {
        var browser = _app.CreateBrowser();
        await SignIn.AsAsync(browser, AppFactory.ManagingDirectorEmail);

        var response = await browser.GetAsync("/Account/ChangePassword");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();

        AssertHasToggle(html, "CurrentPassword", "current-password");
        AssertHasToggle(html, "NewPassword", "new-password");
        AssertHasToggle(html, "ConfirmPassword", "new-password");
    }

    private static void AssertHasToggle(string html, string inputId, string autocomplete)
    {
        // The field is a password field when the page loads, and keeps the
        // autocomplete value the browser's password manager reads.
        var input = Regex.Match(html, $"<input[^>]*\\sid=\"{inputId}\"[^>]*>");
        Assert.True(input.Success, $"No input with id {inputId}.");
        Assert.Contains("type=\"password\"", input.Value);
        Assert.Contains($"autocomplete=\"{autocomplete}\"", input.Value);

        var button = Regex.Match(html, $"<button[^>]*data-password-toggle=\"{inputId}\"[^>]*>");
        Assert.True(button.Success, $"No show password button for {inputId}.");
        Assert.Contains("type=\"button\"", button.Value);
        Assert.Contains($"aria-controls=\"{inputId}\"", button.Value);
        Assert.Contains("aria-pressed=\"false\"", button.Value);
        Assert.Contains("aria-label=\"Show password\"", button.Value);
    }
}
