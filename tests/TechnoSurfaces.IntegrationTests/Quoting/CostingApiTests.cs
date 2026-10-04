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
