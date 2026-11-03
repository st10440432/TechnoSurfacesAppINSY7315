using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Helpers;

namespace TechnoSurfaces.IntegrationTests.FrontEnd;

/// <summary>
/// The costing printout (client request): opened by whoever can open the costing
/// sheet and refused wherever the sheet is refused, carrying the same figures as
/// the sheet, with nothing on the printed document that can be typed into or pressed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CostingPrintTests
{
    private readonly AppFactory _app;

    public CostingPrintTests(AppFactory app) => _app = app;

    private async Task<(int Id, string Reference)> CreateQuoteAsync(string authorEmail)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        var author = await db.Users.SingleAsync(u => u.Email == authorEmail);
        var customer = new Customer { Name = "Print test customer", Contacts = { new Contact { FullName = "Accounts" } } };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var quote = new Quote("PR-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id,
            author.Id, DateOnly.FromDateTime(DateTime.Today));
        quote.StartNewVersion(author.Id, markupPercent: 40m);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return (quote.Id, quote.Reference);
    }

    private async Task<int> RateItemIdAsync(string name)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        return await db.RateItems.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    private static string Document(string html)
    {
        var article = Regex.Match(html, "<article class=\"doc costing-print\".*?</article>", RegexOptions.Singleline);
        Assert.True(article.Success, "The printout has no document.");
        return article.Value;
    }

    [Theory]
    [InlineData(AppFactory.EstimatorEmail)]
    [InlineData(AppFactory.ManagingDirectorEmail)]
    public async Task The_estimator_who_wrote_it_and_the_managing_director_can_open_it(string email)
    {
        var (quoteId, reference) = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var session = await ApiSession.ForAsync(_app, email);

        var response = await session.GetAsync($"/Quotes/CostingPrint/{quoteId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = Document(await response.Content.ReadAsStringAsync());
        Assert.Contains(reference, document);
        Assert.Contains("Print test customer", document);
        Assert.Contains("Not for the customer", document);
    }

    [Fact]
    public async Task It_carries_the_same_lines_and_totals_as_the_costing_sheet_as_plain_text()
    {
        var (quoteId, _) = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var session = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var added = await session.PostAsync($"/api/quotes/{quoteId}/lines",
            new { type = "rate", rateItemId = await RateItemIdAsync("Sanding time"), quantity = 3m });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        var totals = await (await session.GetAsync($"/api/quotes/{quoteId}/totals")).Content.ReadFromJsonAsync<JsonElement>();

        var document = Document(await (await session.GetAsync($"/Quotes/CostingPrint/{quoteId}")).Content.ReadAsStringAsync());

        Assert.Contains("Sanding time", document);
        foreach (var figure in new[] { "subTotalExVat", "markupAmount", "totalExVat", "vatAmount", "totalIncVat" })
            Assert.Contains(Fmt.Rand(totals.GetProperty(figure).GetDecimal()), WebUtility.HtmlDecode(document));

        // Values print as text: nothing on the document can be typed into or pressed.
        Assert.DoesNotContain("<input", document);
        Assert.DoesNotContain("<button", document);
        Assert.DoesNotContain("<select", document);
    }

    [Fact]
    public async Task It_is_refused_signed_out_as_the_costing_sheet_is()
    {
        var (quoteId, _) = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var browser = _app.CreateBrowser();

        var sheet = await browser.GetAsync($"/Quotes/Costing/{quoteId}");
        var print = await browser.GetAsync($"/Quotes/CostingPrint/{quoteId}");

        Assert.Equal(HttpStatusCode.Redirect, sheet.StatusCode);
        Assert.Equal(sheet.StatusCode, print.StatusCode);
        Assert.StartsWith("/Account/Login", print.Headers.Location!.PathAndQuery);
    }

    [Fact]
    public async Task A_quote_that_does_not_exist_goes_back_to_the_quote_list_as_the_costing_sheet_does()
    {
        var session = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);

        var sheet = await session.GetAsync("/Quotes/Costing/999999");
        var print = await session.GetAsync("/Quotes/CostingPrint/999999");

        Assert.Equal(HttpStatusCode.Redirect, print.StatusCode);
        Assert.Equal(sheet.Headers.Location, print.Headers.Location);
    }
}
