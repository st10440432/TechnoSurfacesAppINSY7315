using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Quoting;

/// <summary>
/// The Pastel invoice over HTTP against SQL Server (US-25): the Managing Director
/// records it against an accepted quote; an estimator can read it but not record it.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class InvoiceRecordApiTests
{
    private readonly AppFactory _app;

    public InvoiceRecordApiTests(AppFactory app) => _app = app;

    /// <summary>An accepted quote of the Managing Director's, set up directly in the database.</summary>
    private async Task<int> AcceptedQuoteAsync()
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        var md = await db.Users.SingleAsync(u => u.Email == AppFactory.ManagingDirectorEmail);
        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync(c => c.AccountCode == "RAW001");
        var sanding = await db.RateItems.SingleAsync(r => r.Name == "Sanding time");

        var quote = new Quote("IT-INV-" + Guid.NewGuid().ToString("N")[..8], customer.Id, customer.Contacts.First().Id,
            md.Id, DateOnly.FromDateTime(DateTime.Today).AddDays(-3));
        var version = quote.StartNewVersion(md.Id, markupPercent: 0m);
        version.AddCostingLine(CostingLine.ForRate(sanding.Id, "Sanding time", 100m, "Rate card", 10m, isBelowTheLine: false));
        quote.Approve(md.Id);
        quote.MarkSent();
        quote.MarkAccepted();

        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return quote.Id;
    }

    private static object Invoice(string number, decimal amount) => new
    {
        invoiceNumber = number,
        invoiceDate = DateOnly.FromDateTime(DateTime.Today).AddDays(-1),
        amountIncVat = amount
    };

    [Fact]
    public async Task The_managing_director_records_the_invoice_and_an_estimator_can_read_it()
    {
        var id = await AcceptedQuoteAsync();
        var md = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var number = "IN" + Random.Shared.Next(100000, 999999);

        Assert.Equal(HttpStatusCode.NotFound, (await estimator.GetAsync($"/api/quotes/{id}/invoice")).StatusCode);
        var recorded = await md.PostAsync($"/api/quotes/{id}/invoice", Invoice(number, 1150m));
        var read = await estimator.GetAsync($"/api/quotes/{id}/invoice");
        var body = await read.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Created, recorded.StatusCode);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(number, body.GetProperty("invoiceNumber").GetString());
        Assert.Equal(1150m, body.GetProperty("quotedTotalIncVat").GetDecimal());
        Assert.Equal(0m, body.GetProperty("variance").GetDecimal());
    }

    [Fact]
    public async Task An_estimator_cannot_record_the_invoice()
    {
        var id = await AcceptedQuoteAsync();
        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);

        var response = await estimator.PostAsync($"/api/quotes/{id}/invoice", Invoice("IN-EST-1", 100m));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task A_second_invoice_on_the_same_quote_is_409()
    {
        var id = await AcceptedQuoteAsync();
        var md = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        await md.PostAsync($"/api/quotes/{id}/invoice", Invoice("IN" + Random.Shared.Next(100000, 999999), 100m));

        var again = await md.PostAsync($"/api/quotes/{id}/invoice", Invoice("IN" + Random.Shared.Next(100000, 999999), 100m));

        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
    }
}
