using System.Net;
using System.Text.Json;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Identity;
using Xunit.Abstractions;

namespace TechnoSurfaces.IntegrationTests.Performance;

/// <summary>
/// The number of SQL commands each costing sheet request sends (NFR-05: price lookup
/// and recalculation within two seconds). Each command is a round trip to Azure SQL,
/// so a request that sends a hundred of them is slow on the Basic tier whatever the
/// size of each query. A budget that is exceeded fails CI, so a change that brings
/// the round trips back is caught before it reaches staging.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CostingQueryBudgetTests
{
    private readonly AppFactory _app;
    private readonly ITestOutputHelper _output;

    public CostingQueryBudgetTests(AppFactory app, ITestOutputHelper output)
    {
        _app = app;
        _output = output;
    }

    /// <summary>A quote with one material line and the two most used rate lines, as an estimator's draft.</summary>
    private async Task<(int QuoteId, int LineId)> QuoteWithLinesAsync(HttpClient browser, string token)
    {
        int quoteId, colourId, sheetSizeId, fabricationId, sandingId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            var today = DateOnly.FromDateTime(DateTime.Today);

            var author = await db.Users.SingleAsync(u => u.Email == AppFactory.EstimatorEmail);
            var customer = new Customer { Name = "Query budget customer", Contacts = { new Contact { FullName = "Accounts" } } };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var quote = new Quote("QB-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id,
                author.Id, today);
            quote.StartNewVersion(author.Id, markupPercent: 40m);
            db.Quotes.Add(quote);
            await db.SaveChangesAsync();
            quoteId = quote.Id;

            var price = await db.MaterialPrices
                .Where(p => p.ColourId != null && p.EffectiveFrom <= today && p.EffectiveTo == null
                         && p.Colour!.Status == CatalogueStatus.Active)
                .OrderBy(p => p.Id)
                .FirstAsync();
            colourId = price.ColourId!.Value;
            sheetSizeId = price.SheetSizeId;

            fabricationId = await db.RateItems.Where(r => r.Name == "Fabrication — no backsplash, normal").Select(r => r.Id).SingleAsync();
            sandingId = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();
        }

        var material = await PostAsync(browser, token, $"/api/quotes/{quoteId}/lines",
            new { type = "material", colourId, sheetSizeId, quantity = 3m });
        Assert.Equal(HttpStatusCode.Created, material.StatusCode);
        var lineId = (await material.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("line").GetProperty("id").GetInt32();

        foreach (var rateItemId in new[] { fabricationId, sandingId })
        {
            var rate = await PostAsync(browser, token, $"/api/quotes/{quoteId}/lines", new { type = "rate", rateItemId, quantity = 4m });
            Assert.Equal(HttpStatusCode.Created, rate.StatusCode);
        }

        return (quoteId, lineId);
    }

    private static Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string token, string path, T body) =>
        SendAsync(client, token, HttpMethod.Post, path, body);

    private static Task<HttpResponseMessage> SendAsync<T>(HttpClient client, string token, HttpMethod method, string path, T body)
    {
        var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("RequestVerificationToken", token);
        return client.SendAsync(request);
    }

    /// <summary>Sends one request and returns how many SQL commands it took.</summary>
    private async Task<int> CountAsync(QueryCounter counter, string what, Func<Task<HttpResponseMessage>> send)
    {
        counter.Reset();
        var response = await send();
        var count = counter.Count;

        Assert.True(response.IsSuccessStatusCode, $"{what} returned {(int)response.StatusCode}.");
        _output.WriteLine($"{what}: {count} SQL commands");
        return count;
    }

    [Fact]
    public async Task Each_costing_sheet_request_stays_within_its_query_budget()
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

        var (quoteId, lineId) = await QuoteWithLinesAsync(browser, token);

        var page = await CountAsync(counter, "Costing sheet page", () => browser.GetAsync($"/Quotes/Costing/{quoteId}"));
        var edit = await CountAsync(counter, "Change a quantity", () =>
            SendAsync(browser, token, HttpMethod.Put, $"/api/quotes/{quoteId}/lines/{lineId}", new { quantity = 4m }));
        var totals = await CountAsync(counter, "Totals", () => browser.GetAsync($"/api/quotes/{quoteId}/totals"));
        var refresh = await CountAsync(counter, "Refresh the line tables", () => browser.GetAsync($"/Quotes/CostingLines/{quoteId}"));

        _output.WriteLine($"One edit as the costing sheet makes it (change, totals, refresh): {edit + totals + refresh} SQL commands");

        Assert.True(page <= PageBudget, $"The costing sheet page took {page} SQL commands; the budget is {PageBudget}.");
        Assert.True(edit <= EditBudget, $"Changing a quantity took {edit} SQL commands; the budget is {EditBudget}.");
        Assert.True(refresh <= RefreshBudget, $"Refreshing the line tables took {refresh} SQL commands; the budget is {RefreshBudget}.");
    }

    private const int PageBudget = 20;
    private const int EditBudget = 8;
    private const int RefreshBudget = 18;
}
