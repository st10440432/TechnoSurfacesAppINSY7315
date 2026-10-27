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
/// The quote list and the dashboard must not get slower as quotes build up. Two years
/// at 50 to 100 quotes a month (NFR-08) is over a thousand quotes, so the number of
/// SQL commands each page sends has to stay the same however many quotes there are:
/// the list is paged, and only each quote's current version is loaded.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class QuoteListQueryTests
{
    private readonly AppFactory _app;
    private readonly ITestOutputHelper _output;

    public QuoteListQueryTests(AppFactory app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    /// <summary>Quotes by the Managing Director, each reopened once so it has two versions, each with a costing line.</summary>
    private async Task AddQuotesAsync(int howMany)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        var author = await db.Users.SingleAsync(u => u.Email == AppFactory.ManagingDirectorEmail);
        var sanding = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();
        // One customer for every batch, so the list's customer filter does not grow with them.
        var customer = await db.Customers.Include(c => c.Contacts).FirstOrDefaultAsync(c => c.Name == "Quote list customer");
        if (customer is null)
        {
            customer = new Customer { Name = "Quote list customer", Contacts = { new Contact { FullName = "Accounts" } } };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
        }

        var today = DateOnly.FromDateTime(DateTime.Today);
        for (var i = 0; i < howMany; i++)
        {
            var quote = new Quote("QL-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id,
                author.Id, today);
            foreach (var markup in new[] { 40m, 35m })
                quote.StartNewVersion(author.Id, markup)
                    .AddCostingLine(CostingLine.ForRate(sanding, "Sanding time", 100m, "Rate card: Sanding time", 2m, isBelowTheLine: false));
            db.Quotes.Add(quote);
        }
        await db.SaveChangesAsync();
    }

    private static async Task<(int Commands, int Rows)> MeasureAsync(HttpClient browser, QueryCounter counter, string path)
    {
        counter.Reset();
        var response = await browser.GetAsync(path);
        var measured = (counter.Count, counter.RowsRead);

        Assert.True(response.IsSuccessStatusCode, $"{path} returned {(int)response.StatusCode}.");
        return measured;
    }

    [Fact]
    public async Task The_quote_list_and_dashboard_do_not_read_more_for_every_quote_added()
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
        await SignIn.AsAsync(browser, AppFactory.ManagingDirectorEmail);

        // More than a page of quotes first, so the newest page is these quotes both times.
        await AddQuotesAsync(30);
        var list = await MeasureAsync(browser, counter, "/Quotes/Index");
        var dashboard = await MeasureAsync(browser, counter, "/Home/Dashboard");

        await AddQuotesAsync(30);
        var listAfter = await MeasureAsync(browser, counter, "/Quotes/Index");
        var secondPage = await MeasureAsync(browser, counter, "/Quotes/Index?page=2");
        var dashboardAfter = await MeasureAsync(browser, counter, "/Home/Dashboard");

        _output.WriteLine($"Quote list: {list.Commands} commands, {list.Rows} rows; with 30 more quotes {listAfter.Commands} commands, {listAfter.Rows} rows");
        _output.WriteLine($"Quote list page 2: {secondPage.Commands} commands, {secondPage.Rows} rows");
        _output.WriteLine($"Dashboard: {dashboard.Commands} commands, {dashboard.Rows} rows; with 30 more quotes {dashboardAfter.Commands} commands, {dashboardAfter.Rows} rows");

        // The list shows one page: the same commands and the same rows however many quotes exist.
        Assert.Equal(list, listAfter);
        Assert.Equal(list.Commands, secondPage.Commands);

        // The dashboard totals every quote, but reads only its current version: the quote,
        // that version and its one line, three rows a quote. Every version would be five.
        Assert.Equal(dashboard.Commands, dashboardAfter.Commands);
        Assert.True(dashboardAfter.Rows - dashboard.Rows <= 30 * 3,
            $"The dashboard read {dashboardAfter.Rows - dashboard.Rows} more rows for 30 more quotes; at most 90 are needed.");
    }
}
