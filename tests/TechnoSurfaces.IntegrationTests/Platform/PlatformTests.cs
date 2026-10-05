using System.Net;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Platform;

namespace TechnoSurfaces.IntegrationTests.Platform;

/// <summary>
/// The hosting and platform behaviour the deployment depends on: the health check
/// the pipeline gates on, the security headers, sign-in rate limiting, the error
/// page, and the global rule that nothing but sign-in is reachable without signing in.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class PlatformTests
{
    private readonly AppFactory _app;

    public PlatformTests(AppFactory app) => _app = app;

    [Fact]
    public async Task Health_is_healthy_once_the_database_is_migrated_and_needs_no_sign_in()
    {
        var response = await _app.CreateBrowser().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_anonymous_request_is_sent_to_sign_in()
    {
        var response = await _app.CreateBrowser().GetAsync("/Home/Dashboard");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/Login", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task There_is_no_registration_route()
    {
        // Signed in first: the global sign-in rule also covers addresses that match
        // no page, so an anonymous request would be sent to sign-in before routing
        // could show that the page does not exist.
        var browser = _app.CreateBrowser();
        await SignIn.AsAsync(browser, AppFactory.ManagingDirectorEmail);

        var response = await browser.GetAsync("/Account/Register");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Every_response_carries_the_security_headers()
    {
        var response = await _app.CreateBrowser().GetAsync("/Account/Login");
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();

        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", response.Headers.GetValues("Referrer-Policy").Single());
    }

    [Fact]
    public async Task The_error_page_needs_no_sign_in_and_shows_no_developer_detail()
    {
        var response = await _app.CreateBrowser().GetAsync("/Error");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Something went wrong", html);
        Assert.DoesNotContain("Development Mode", html);
    }

    [Theory]
    [InlineData("/Account/ForgotPassword")]
    [InlineData("/Account/Activate")]
    public async Task The_password_help_pages_need_no_sign_in_and_send_nothing(string path)
    {
        // There is no email service (Task 1 8.2): the Managing Director issues a
        // temporary password. These pages explain that and take no input.
        var response = await _app.CreateBrowser().GetAsync(path);
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("temporary password", html);
        Assert.DoesNotContain("<form", html);
    }

    [Fact]
    public async Task A_seeded_demo_account_can_sign_in_and_reach_the_dashboard()
    {
        var browser = _app.CreateBrowser();

        await SignIn.AsAsync(browser, AppFactory.ManagingDirectorEmail);
        var dashboard = await browser.GetAsync("/Home/Dashboard");

        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
    }

    [Fact]
    public async Task Repeated_sign_in_attempts_from_one_address_are_rate_limited()
    {
        // A separate server instance, so this test's attempts do not use up the
        // allowance the other tests in the run sign in with.
        await using var isolated = _app.WithWebHostBuilder(_ => { });
        var client = isolated.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });

        var statuses = new List<HttpStatusCode>();
        for (var attempt = 0; attempt <= SignInRateLimiting.PermitLimit; attempt++)
        {
            var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = "nobody@example.com",
                ["Password"] = "wrong-password"
            }));
            statuses.Add(response.StatusCode);
        }

        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(SignInRateLimiting.PermitLimit));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses.Last());
    }
}