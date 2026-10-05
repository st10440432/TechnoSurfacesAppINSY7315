using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Identity;

namespace TechnoSurfaces.IntegrationTests.Security;

/// <summary>
/// The security tests from the Task 2 build plan that the API suites do not already
/// cover, run against the real application and a real SQL Server database.
///
/// Each test starts its own server instance, because sign-in is rate limited per
/// client address and every test client shares one. All instances share one
/// database, so no test here locks or changes an account another suite signs in with.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class SecurityTests
{
    private const string DeactivatedEmail = "renaldo@technosurfaces.co.za";

    private readonly AppFactory _app;

    public SecurityTests(AppFactory app) => _app = app;

    private static HttpClient Browser(WebApplicationFactory<Program> server) =>
        server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });

    /// <summary>Posts a form as the signed-in user, with that user's antiforgery token unless told not to.</summary>
    private static async Task<HttpResponseMessage> PostFormAsync(
        HttpClient client, string path, Dictionary<string, string> fields, bool withToken = true)
    {
        if (withToken)
            fields["__RequestVerificationToken"] = await SignIn.AntiforgeryTokenAsync(client, "/Home/Dashboard");

        return await client.PostAsync(path, new FormUrlEncodedContent(fields));
    }

    /// <summary>A rate on the card that is entered, not derived, and not supplier-specific.</summary>
    private static async Task<int> EnteredRateItemAsync(WebApplicationFactory<Program> server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        return await db.RatePrices
            .Where(p => p.SupplierId == null && p.EffectiveTo == null && p.RateItem!.DerivedFromRateItemId == null)
            .Select(p => p.RateItemId)
            .FirstAsync();
    }

    // Each test uses its own amount, so they cannot affect one another.
    private static Dictionary<string, string> NewRate(int rateItemId, string amount) => new()
    {
        ["RateItemId"] = rateItemId.ToString(),
        ["Amount"] = amount,
        ["EffectiveFrom"] = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7).ToString("yyyy-MM-dd")
    };

    private static async Task<bool> RateExistsAsync(WebApplicationFactory<Program> server, int rateItemId, decimal amount)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        return await db.RatePrices.AnyAsync(p => p.RateItemId == rateItemId && p.Amount == amount);
    }

    [Fact]
    public async Task A_price_change_by_the_md_is_audited_with_the_user_and_time()
    {
        await using var server = _app.WithWebHostBuilder(_ => { });
        var md = Browser(server);
        await SignIn.AsAsync(md, AppFactory.ManagingDirectorEmail);
        var rateItemId = await EnteredRateItemAsync(server);
        var before = DateTime.UtcNow.AddSeconds(-1);

        await PostFormAsync(md, "/Catalogue/SetRate", NewRate(rateItemId, "999"));

        Assert.True(await RateExistsAsync(server, rateItemId, 999m));

        using var scope = server.Services.CreateScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
        var mdId = (await users.FindByEmailAsync(AppFactory.ManagingDirectorEmail))!.Id;
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        var entries = await db.AuditEntries
            .Where(a => a.EntityName == nameof(RatePrice) && a.ChangedAtUtc >= before)
            .ToListAsync();

        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.Equal(mdId, e.UserId));
    }

    [Fact]
    public async Task An_estimator_cannot_change_a_price()
    {
        await using var server = _app.WithWebHostBuilder(_ => { });
        var estimator = Browser(server);
        await SignIn.AsAsync(estimator, AppFactory.EstimatorEmail);
        var rateItemId = await EnteredRateItemAsync(server);

        var response = await PostFormAsync(estimator, "/Catalogue/SetRate", NewRate(rateItemId, "777"));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", response.Headers.Location!.PathAndQuery);
        Assert.False(await RateExistsAsync(server, rateItemId, 777m));
    }

    [Theory]
    [InlineData("/Admin/Users")]
    [InlineData("/Admin/Audit")]
    public async Task An_estimator_cannot_open_the_md_only_screens(string path)
    {
        await using var server = _app.WithWebHostBuilder(_ => { });
        var estimator = Browser(server);
        await SignIn.AsAsync(estimator, AppFactory.EstimatorEmail);

        var response = await estimator.GetAsync(path);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", response.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task A_form_post_without_an_antiforgery_token_is_rejected()
    {
        await using var server = _app.WithWebHostBuilder(_ => { });
        var md = Browser(server);
        await SignIn.AsAsync(md, AppFactory.ManagingDirectorEmail);
        var rateItemId = await EnteredRateItemAsync(server);

        var response = await PostFormAsync(md, "/Catalogue/SetRate", NewRate(rateItemId, "555"), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.False(await RateExistsAsync(server, rateItemId, 555m));
    }

    [Fact]
    public async Task A_deactivated_user_cannot_sign_in_even_with_the_right_password()
    {
        await using var server = _app.WithWebHostBuilder(_ => { });
        var client = Browser(server);

        var response = await SignIn.PostAsync(client, DeactivatedEmail, AppFactory.DemoPassword);
        var after = await client.GetAsync("/Home/Dashboard");

        Assert.NotEqual(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, after.StatusCode);
        Assert.StartsWith("/Account/Login", after.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task Five_wrong_passwords_lock_the_account()
    {
        await using var server = _app.WithWebHostBuilder(_ => { });

        // A throwaway account: locking a demo account would break the other suites
        // that sign in as it, because every server in the run shares one database.
        const string email = "lockout-test@technosurfaces.co.za";
        using (var scope = server.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
            var created = await users.CreateAsync(
                new UserAccount { UserName = email, Email = email, MustChangePassword = false },
                AppFactory.DemoPassword);
            Assert.True(created.Succeeded);
        }

        var client = Browser(server);
        for (var attempt = 0; attempt < 5; attempt++)
            await SignIn.PostAsync(client, email, "Wrong-Password-2026");

        using (var scope = server.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<UserAccount>>();
            var account = await users.FindByEmailAsync(email);
            Assert.True(await users.IsLockedOutAsync(account!));
        }
    }
}