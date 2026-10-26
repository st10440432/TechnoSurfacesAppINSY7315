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

    /// <summary>
    /// A quote with one material line and the two most used rate lines, as an
    /// estimator's draft. Returns the material line and the last rate line.
    /// </summary>
    private async Task<(int QuoteId, int MaterialLineId, int RateLineId)> QuoteWithLinesAsync(HttpClient browser, string token)
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
        var materialLineId = await LineIdAsync(material);

        var rateLineId = 0;
        foreach (var rateItemId in new[] { fabricationId, sandingId })
        {
            var rate = await PostAsync(browser, token, $"/api/quotes/{quoteId}/lines", new { type = "rate", rateItemId, quantity = 4m });
            Assert.Equal(HttpStatusCode.Created, rate.StatusCode);
            rateLineId = await LineIdAsync(rate);
        }

        return (quoteId, materialLineId, rateLineId);
    }

    private static async Task<int> LineIdAsync(HttpResponseMessage created) =>
        (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("line").GetProperty("id").GetInt32();

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

        var (quoteId, materialLineId, rateLineId) = await QuoteWithLinesAsync(browser, token);

        var page = await CountAsync(counter, "Costing sheet page", () => browser.GetAsync($"/Quotes/Costing/{quoteId}"));

        // What the costing sheet sends for each kind of edit: the change, then the
        // refresh of the line tables. The change answers with the new totals, so the
        // sheet does not call /totals as well.
        var change = await CountAsync(counter, "Change a quantity", () =>
            SendAsync(browser, token, HttpMethod.Put, $"/api/quotes/{quoteId}/lines/{materialLineId}", new { quantity = 4m }));
        var refresh = await CountAsync(counter, "Refresh the line tables", () => browser.GetAsync($"/Quotes/CostingLines/{quoteId}"));

        HttpResponseMessage? removed = null;
        var remove = await CountAsync(counter, "Remove a line", async () =>
            removed = await SendAsync(browser, token, HttpMethod.Delete, $"/api/quotes/{quoteId}/lines/{rateLineId}", new { }));
        Assert.True((await removed!.Content.ReadFromJsonAsync<JsonElement>()).TryGetProperty("totals", out _),
            "Removing a line must answer with the new totals, so the sheet need not ask for them.");

        _output.WriteLine($"Changing a quantity as the costing sheet does it: {change + refresh} SQL commands");
        _output.WriteLine($"Removing a line as the costing sheet does it: {remove + refresh} SQL commands");

        Assert.True(page <= PageBudget, $"The costing sheet page took {page} SQL commands; the budget is {PageBudget}.");
        Assert.True(change <= ChangeBudget, $"Changing a quantity took {change} SQL commands; the budget is {ChangeBudget}.");
        Assert.True(remove <= RemoveBudget, $"Removing a line took {remove} SQL commands; the budget is {RemoveBudget}.");
        Assert.True(refresh <= RefreshBudget, $"Refreshing the line tables took {refresh} SQL commands; the budget is {RefreshBudget}.");
    }

    private const int PageBudget = 17;
    private const int ChangeBudget = 8;
    private const int RemoveBudget = 8;
    private const int RefreshBudget = 15;
}
