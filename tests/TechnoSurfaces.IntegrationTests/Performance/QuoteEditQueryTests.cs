using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Identity;
using Xunit.Abstractions;

namespace TechnoSurfaces.IntegrationTests.Performance;

/// <summary>
/// Editing a quote touches only its current version, so a quote reopened after
/// several counter-offers must cost no more to change than one reopened once
/// (NFR-05). Earlier versions are read only by the version history.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class QuoteEditQueryTests
{
    private readonly AppFactory _app;
    private readonly ITestOutputHelper _output;

    public QuoteEditQueryTests(AppFactory app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    /// <summary>An estimator's draft with the given number of versions, each with one line. Returns the current version's line.</summary>
    private async Task<(int QuoteId, int LineId)> QuoteWithVersionsAsync(int versions)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        var author = await db.Users.SingleAsync(u => u.Email == AppFactory.EstimatorEmail);
        var sanding = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();
        var customer = new Customer { Name = "Quote edit customer", Contacts = { new Contact { FullName = "Accounts" } } };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var quote = new Quote("QE-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id,
            author.Id, DateOnly.FromDateTime(DateTime.Today));
        for (var i = 0; i < versions; i++)
            quote.StartNewVersion(author.Id, markupPercent: 40m)
                .AddCostingLine(CostingLine.ForRate(sanding, "Sanding time", 100m, "Rate card: Sanding time", 2m, isBelowTheLine: false));
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();

        return (quote.Id, quote.CurrentVersion!.CostingLines.Single().Id);
    }

    [Fact]
    public async Task Changing_a_line_reads_the_same_rows_however_many_versions_the_quote_has()
    {
        var counter = new QueryCounter();
        await using var server = _app.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.ConfigureDbContext<TechnoSurfacesDbContext>(options => options.AddInterceptors(counter));
            services.ConfigureDbContext<AuthDbContext>(options => options.AddInterceptors(counter));
        }));
        var browser = server.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true,
            BaseAddress = new Uri("https://localhost")
        });
        await SignIn.AsAsync(browser, AppFactory.EstimatorEmail);
        var token = await SignIn.AntiforgeryTokenAsync(browser, "/Home/Dashboard");

        async Task<(int Commands, int Rows)> ChangeAsync(int quoteId, int lineId)
        {
            var request = new HttpRequestMessage(HttpMethod.Put, $"/api/quotes/{quoteId}/lines/{lineId}")
            {
                Content = JsonContent.Create(new { quantity = 3m })
            };
            request.Headers.Add("RequestVerificationToken", token);

            counter.Reset();
            var response = await browser.SendAsync(request);
            var measured = (counter.Count, counter.RowsRead);
            Assert.True(response.IsSuccessStatusCode, $"Changing the line returned {(int)response.StatusCode}.");
            return measured;
        }

        var (twoVersions, lineOfTwo) = await QuoteWithVersionsAsync(2);
        var (sixVersions, lineOfSix) = await QuoteWithVersionsAsync(6);

        var few = await ChangeAsync(twoVersions, lineOfTwo);
        var many = await ChangeAsync(sixVersions, lineOfSix);

        _output.WriteLine($"Changing a line, 2 versions: {few.Commands} commands, {few.Rows} rows");
        _output.WriteLine($"Changing a line, 6 versions: {many.Commands} commands, {many.Rows} rows");

        Assert.Equal(few, many);
    }
}
