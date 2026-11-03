using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Quoting;

/// <summary>
/// The costing sheet API over real HTTP against SQL Server: who may change a quote
/// (CanEditQuote), and the status codes the costing screen relies on. A price that
/// cannot be resolved is 422 and saves nothing (NFR-01, US-03).
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CostingApiTests
{
    private const string OtherEstimatorEmail = "devan@technosurfaces.co.za";

    private readonly AppFactory _app;

    public CostingApiTests(AppFactory app) => _app = app;

    /// <summary>A Draft quote by the given author, submitted for approval when asked.</summary>
    private async Task<int> CreateQuoteAsync(string authorEmail, bool submit = false)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();

        var author = await db.Users.SingleAsync(u => u.Email == authorEmail);
        var customer = new Customer { Name = "Integration customer", Contacts = { new Contact { FullName = "Accounts" } } };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var quote = new Quote("IT-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id,
            author.Id, DateOnly.FromDateTime(DateTime.Today));
        quote.StartNewVersion(author.Id, markupPercent: 40m);
        if (submit)
            quote.Submit();

        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return quote.Id;
    }

    private async Task<int> RateItemIdAsync(string name)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        return await db.RateItems.Where(r => r.Name == name).Select(r => r.Id).SingleAsync();
    }

    private async Task<int> LineCountAsync(int quoteId)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        return await db.CostingLines.CountAsync(l =>
            db.QuoteVersions.Any(v => v.Id == l.QuoteVersionId && v.QuoteId == quoteId));
    }

    private Task<ApiSession> SignedInAsync(string email) => ApiSession.ForAsync(_app, email);

    private static Task<HttpResponseMessage> AddRateLineAsync(ApiSession session, int quoteId, int rateItemId,
        decimal? unitPrice = null, bool withToken = true) =>
        session.PostAsync($"/api/quotes/{quoteId}/lines",
            new { type = "rate", rateItemId, quantity = 2m, unitPrice }, withToken);

    private static async Task<JsonElement> ProblemAsync(HttpResponseMessage response)
    {
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task An_anonymous_call_is_401_not_a_redirect_to_sign_in()
    {
        var response = await _app.CreateBrowser().GetAsync("/api/catalogue/suppliers");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_estimator_adds_a_line_to_their_own_draft()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains($"/api/quotes/{quoteId}/lines/", response.Headers.Location!.ToString());
        Assert.Equal(1, await LineCountAsync(quoteId));
    }

    [Fact]
    public async Task The_costing_is_copied_to_the_quotation_and_adds_up_to_the_costing_total()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);
        await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));

        var response = await client.PostAsync($"/api/quotes/{quoteId}/quotation-lines/from-costing");
        var check = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("check");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(check.GetProperty("matches").GetBoolean());
    }

    private async Task<JsonElement> QuotationLinesAsync(ApiSession session, int quoteId) =>
        (await (await session.GetAsync($"/api/quotes/{quoteId}/quotation")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("lines");

    [Fact]
    public async Task The_copied_lines_follow_the_costing_sheet_and_are_never_copied_twice()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);
        int colourId, sheetSizeId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var price = await db.MaterialPrices
                .Where(p => p.ColourId != null && p.EffectiveFrom <= today && p.EffectiveTo == null
                         && p.Colour!.Status == TechnoSurfaces.Domain.CatalogueStatus.Active)
                .OrderBy(p => p.Id)
                .FirstAsync();
            (colourId, sheetSizeId) = (price.ColourId!.Value, price.SheetSizeId);
        }
        var added = await client.PostAsync($"/api/quotes/{quoteId}/lines", new { type = "material", colourId, sheetSizeId, quantity = 2m });
        var materialLineId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("line").GetProperty("id").GetInt32();
        await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));

        await client.PostAsync($"/api/quotes/{quoteId}/quotation-lines/from-costing");
        await client.PostAsync($"/api/quotes/{quoteId}/quotation-lines/from-costing");
        Assert.Equal(2, (await QuotationLinesAsync(client, quoteId)).GetArrayLength());

        // Sheet counts on, then a new sheet count on the costing sheet.
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsync($"/api/quotes/{quoteId}/quotation/sheet-counts", new { showSheetCounts = true })).StatusCode);
        await client.PutAsync($"/api/quotes/{quoteId}/lines/{materialLineId}", new { quantity = 5m });
        Assert.Equal(5m, (await QuotationLinesAsync(client, quoteId))[0].GetProperty("quantity").GetDecimal());

        // Removing the material on SQL Server removes its quotation line first.
        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/quotes/{quoteId}/lines/{materialLineId}")).StatusCode);
        Assert.Equal(1, (await QuotationLinesAsync(client, quoteId)).GetArrayLength());
        var check = await (await client.GetAsync($"/api/quotes/{quoteId}/quotation/check")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(check.GetProperty("matches").GetBoolean());
    }

    [Fact]
    public async Task An_item_is_costed_on_its_own_and_counted_as_often_as_it_is_needed()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);
        var sandingId = await RateItemIdAsync("Sanding time");
        await AddRateLineAsync(client, quoteId, sandingId);

        var created = await client.PostAsync($"/api/quotes/{quoteId}/items", new { name = "Vanity", number = "2", quantity = 3m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();

        var line = await client.PostAsync($"/api/quotes/{quoteId}/items/{itemId}/lines", new { type = "rate", rateItemId = sandingId, quantity = 1m });
        Assert.Equal(HttpStatusCode.Created, line.StatusCode);
        var markup = await client.PutAsync($"/api/quotes/{quoteId}/items/{itemId}/costing", new { markupPercent = 10m });
        var totals = await markup.Content.ReadFromJsonAsync<JsonElement>();

        // Item 1: two hours of sanding at 40%. The vanity: one hour at 10%, three times.
        var items = totals.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        var vanity = items.Single(i => i.GetProperty("itemId").GetInt32() == itemId);
        Assert.Equal(3m * vanity.GetProperty("totalExVat").GetDecimal(), vanity.GetProperty("amountExVat").GetDecimal());
        Assert.Equal(items.Sum(i => i.GetProperty("amountExVat").GetDecimal()), totals.GetProperty("totalExVat").GetDecimal());

        var page = await client.GetAsync($"/Quotes/Costing/{quoteId}?item={itemId}");
        Assert.Contains("Vanity", await page.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await client.DeleteAsync($"/api/quotes/{quoteId}/items/{itemId}")).StatusCode);
        var only = (await (await client.GetAsync($"/api/quotes/{quoteId}/costing")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("items");
        Assert.Equal(1, only.GetArrayLength());
        var last = await client.DeleteAsync($"/api/quotes/{quoteId}/items/{only[0].GetProperty("id").GetInt32()}");
        Assert.Equal(HttpStatusCode.UnprocessableEntity, last.StatusCode);
    }

    [Fact]
    public async Task A_bill_of_quantities_lists_each_item_with_the_allowance_spread_over_the_rates()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);
        var sandingId = await RateItemIdAsync("Sanding time");
        await AddRateLineAsync(client, quoteId, sandingId);
        var created = await client.PostAsync($"/api/quotes/{quoteId}/items",
            new { name = "Floor unit, 4919 mm", number = "4", quantity = 2m, unit = "No", section = "Floor units" });
        var itemId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetInt32();
        await client.PostAsync($"/api/quotes/{quoteId}/items/{itemId}/lines", new { type = "rate", rateItemId = sandingId, quantity = 1m });

        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsync($"/api/quotes/{quoteId}/costing", new { allowanceAmount = 1500m })).StatusCode);
        Assert.Equal(HttpStatusCode.OK,
            (await client.PutAsync($"/api/quotes/{quoteId}/quotation/layout", new { layout = "BillOfQuantities" })).StatusCode);

        var document = await (await client.GetAsync($"/api/quotes/{quoteId}/quotation")).Content.ReadFromJsonAsync<JsonElement>();
        var floorUnits = document.GetProperty("bill").GetProperty("sections").EnumerateArray()
            .Single(s => s.GetProperty("heading").GetString() == "Floor units").GetProperty("items")[0];
        Assert.Equal(("4", 2m), (floorUnits.GetProperty("number").GetString(), floorUnits.GetProperty("quantity").GetDecimal()));
        Assert.Equal(2m * floorUnits.GetProperty("amountEach").GetDecimal(), floorUnits.GetProperty("amountExVat").GetDecimal());
        Assert.Equal(0, document.GetProperty("bill").GetProperty("extras").GetArrayLength());

        var check = await (await client.GetAsync($"/api/quotes/{quoteId}/quotation/check")).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(check.GetProperty("matches").GetBoolean());

        var bad = await client.PutAsync($"/api/quotes/{quoteId}/quotation/layout", new { layout = "Spreadsheet" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var page = await client.GetAsync($"/Quotes/Quotation/{quoteId}");
        Assert.Contains("Floor unit, 4919 mm", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Another_estimator_cannot_change_the_quotation_layout()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var other = await SignedInAsync(OtherEstimatorEmail);

        var response = await other.PutAsync($"/api/quotes/{quoteId}/quotation/layout", new { layout = "BillOfQuantities" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_estimator_cannot_add_an_item_to_a_quote()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var other = await SignedInAsync(OtherEstimatorEmail);

        var response = await other.PostAsync($"/api/quotes/{quoteId}/items", new { name = "Vanity", quantity = 1m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_estimator_cannot_change_the_sheet_counts_on_the_quotation()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var other = await SignedInAsync(OtherEstimatorEmail);

        var response = await other.PutAsync($"/api/quotes/{quoteId}/quotation/sheet-counts", new { showSheetCounts = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Another_estimator_cannot_copy_the_costing_to_the_quotation()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var author = await SignedInAsync(AppFactory.EstimatorEmail);
        await AddRateLineAsync(author, quoteId, await RateItemIdAsync("Sanding time"));
        var other = await SignedInAsync(OtherEstimatorEmail);

        var response = await other.PostAsync($"/api/quotes/{quoteId}/quotation-lines/from-costing");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task An_estimator_cannot_change_another_estimators_draft()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(OtherEstimatorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(403, (await ProblemAsync(response)).GetProperty("status").GetInt32());
        Assert.Equal(0, await LineCountAsync(quoteId));
    }

    [Fact]
    public async Task An_estimator_cannot_change_their_quote_once_it_is_submitted()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail, submit: true);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await client.PutAsync($"/api/quotes/{quoteId}/costing", new { markupPercent = 50m });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task The_managing_director_can_correct_a_pending_quote()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail, submit: true);
        var client = await SignedInAsync(AppFactory.ManagingDirectorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(1, await LineCountAsync(quoteId));
    }

    [Fact]
    public async Task A_rate_with_no_price_is_422_and_saves_nothing()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sink / vanity"));

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("Sink / vanity", (await ProblemAsync(response)).GetProperty("detail").GetString());
        Assert.Equal(0, await LineCountAsync(quoteId));
    }

    [Fact]
    public async Task A_rate_with_no_price_takes_a_price_typed_for_the_job()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sink / vanity"), unitPrice: 1250m);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Price entered on the quote", body.GetProperty("line").GetProperty("priceOrigin").GetString());
    }

    [Fact]
    public async Task A_rate_override_of_zero_is_refused_as_invalid()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);
        var added = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"));
        var lineId = (await added.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("line").GetProperty("id").GetInt32();

        var response = await client.PutAsync($"/api/quotes/{quoteId}/lines/{lineId}", new { unitPrice = 0m });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await ProblemAsync(response)).GetProperty("errors").TryGetProperty("UnitPrice", out _));
    }

    [Fact]
    public async Task A_write_without_the_antiforgery_header_is_refused()
    {
        var quoteId = await CreateQuoteAsync(AppFactory.EstimatorEmail);
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await AddRateLineAsync(client, quoteId, await RateItemIdAsync("Sanding time"), withToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await LineCountAsync(quoteId));
    }

    [Fact]
    public async Task An_unknown_quote_is_404()
    {
        var client = await SignedInAsync(AppFactory.EstimatorEmail);

        var response = await AddRateLineAsync(client, 999_999, await RateItemIdAsync("Sanding time"));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
