using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;
using TechnoSurfacesApp.Helpers;

namespace TechnoSurfaces.IntegrationTests.FrontEnd;

/// <summary>
/// The old price list check on the review screen (Task 1 2.4): a supplier on the
/// quote whose price list is more than a year old is named with the date on its list,
/// and with none the check shows as passed.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class OldPriceListWarningTests
{
    private readonly AppFactory _app;

    public OldPriceListWarningTests(AppFactory app) => _app = app;

    /// <summary>A submitted quote by the estimator, with one material line from the named supplier.</summary>
    private async Task<int> QuoteWithMaterialFromAsync(string supplierName)
    {
        int quoteId, colourId, sheetSizeId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            var today = DateOnly.FromDateTime(DateTime.Today);
            var author = await db.Users.SingleAsync(u => u.Email == AppFactory.EstimatorEmail);
            var customer = new Customer { Name = "Price list test customer", Contacts = { new Contact { FullName = "Accounts" } } };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();

            var quote = new Quote("PL-" + Guid.NewGuid().ToString("N")[..10], customer.Id, customer.Contacts.Single().Id, author.Id, today);
            quote.StartNewVersion(author.Id, markupPercent: 40m);
            db.Quotes.Add(quote);
            await db.SaveChangesAsync();
            quoteId = quote.Id;

            var price = await db.MaterialPrices
                .Where(p => p.ColourId != null && p.Colour!.ProductLine!.Supplier!.Name == supplierName
                         && p.EffectiveFrom <= today && p.EffectiveTo == null && p.Colour.Status == CatalogueStatus.Active)
                .OrderBy(p => p.Id)
                .FirstAsync();
            colourId = price.ColourId!.Value;
            sheetSizeId = price.SheetSizeId;
        }

        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var added = await estimator.PostAsync($"/api/quotes/{quoteId}/lines", new { type = "material", colourId, sheetSizeId, quantity = 1m });
        Assert.Equal(HttpStatusCode.Created, added.StatusCode);
        return quoteId;
    }

    private async Task<string> ReviewAsync(int quoteId)
    {
        var director = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        var response = await director.GetAsync($"/Quotes/Review/{quoteId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
    }

    private async Task<DateOnly> SetPriceListDateAsync(string supplierName, DateOnly dated)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        var supplier = await db.Suppliers.SingleAsync(s => s.Name == supplierName);
        var before = supplier.PriceListDated;
        supplier.PriceListDated = dated;
        await db.SaveChangesAsync();
        return before;
    }

    [Fact]
    public async Task A_supplier_whose_price_list_is_over_a_year_old_is_named_with_its_date()
    {
        const string supplier = "Max on Top";
        var listDated = DateOnly.FromDateTime(DateTime.Today).AddYears(-2);
        var original = await SetPriceListDateAsync(supplier, listDated);
        try
        {
            var html = await ReviewAsync(await QuoteWithMaterialFromAsync(supplier));

            Assert.Contains("Old price list", html);
            Assert.Contains($"{supplier}, dated {Fmt.Date(listDated)}", html);
            Assert.DoesNotContain("Price lists up to date", html);
        }
        finally
        {
            await SetPriceListDateAsync(supplier, original);
        }
    }

    [Fact]
    public async Task With_every_supplier_recent_the_check_shows_as_passed()
    {
        const string supplier = "Max on Top";
        var original = await SetPriceListDateAsync(supplier, DateOnly.FromDateTime(DateTime.Today));
        try
        {
            var html = await ReviewAsync(await QuoteWithMaterialFromAsync(supplier));

            Assert.Contains("Price lists up to date", html);
            Assert.DoesNotContain("Old price list", html);
        }
        finally
        {
            await SetPriceListDateAsync(supplier, original);
        }
    }
}
