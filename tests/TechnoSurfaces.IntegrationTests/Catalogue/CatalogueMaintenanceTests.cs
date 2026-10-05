using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Catalogue;

/// <summary>
/// NFR-10 and US-23 through the real screens: the Managing Director adds to the
/// catalogue; an estimator is refused at the server, whatever the page shows.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class CatalogueMaintenanceTests
{
    private readonly AppFactory _app;

    public CatalogueMaintenanceTests(AppFactory app) => _app = app;

    private static Task<HttpResponseMessage> PostFormAsync(ApiSession session, string path, Dictionary<string, string> form)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Add("RequestVerificationToken", session.Token);
        return session.Client.SendAsync(request);
    }

    [Fact]
    public async Task The_md_adds_a_supplier_and_its_materials_and_an_estimator_is_refused()
    {
        var md = await ApiSession.ForAsync(_app, AppFactory.ManagingDirectorEmail);
        var estimator = await ApiSession.ForAsync(_app, AppFactory.EstimatorEmail);
        var name = "IT supplier " + Guid.NewGuid().ToString("N")[..6];

        var refused = await PostFormAsync(estimator, "/Catalogue/AddSupplier", new()
        {
            ["Name"] = name, ["PricingStructure"] = "Item", ["PriceListDated"] = "2026-09-01", ["AdhesivePrice"] = "130"
        });
        Assert.Equal(HttpStatusCode.Redirect, refused.StatusCode);
        Assert.Contains("/Account/AccessDenied", refused.Headers.Location!.ToString());

        var added = await PostFormAsync(md, "/Catalogue/AddSupplier", new()
        {
            ["Name"] = name, ["PricingStructure"] = "Item", ["PriceListDated"] = "2026-09-01", ["AdhesivePrice"] = "130"
        });
        Assert.Equal(HttpStatusCode.Redirect, added.StatusCode);

        int supplierId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            supplierId = await db.Suppliers.Where(s => s.Name == name).Select(s => s.Id).SingleAsync();
        }
        Assert.EndsWith($"/Catalogue/Supplier/{supplierId}", added.Headers.Location!.ToString());

        await PostFormAsync(md, "/Catalogue/AddProductLine", new()
        {
            ["SupplierId"] = supplierId.ToString(), ["Name"] = "Acrylic", ["ThicknessMm"] = "12"
        });

        int lineId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            lineId = await db.ProductLines.Where(l => l.SupplierId == supplierId).Select(l => l.Id).SingleAsync();
        }

        var colourRefused = await PostFormAsync(estimator, "/Catalogue/AddColour", new()
        {
            ["ProductLineId"] = lineId.ToString(), ["Name"] = "Arctic"
        });
        await PostFormAsync(md, "/Catalogue/AddColour", new()
        {
            ["ProductLineId"] = lineId.ToString(), ["Name"] = "Arctic", ["SupplierCode"] = "AR-1"
        });

        Assert.Contains("/Account/AccessDenied", colourRefused.Headers.Location!.ToString());
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            Assert.Equal("AR-1", await db.Colours.Where(c => c.ProductLineId == lineId).Select(c => c.SupplierCode).SingleAsync());
        }

        // Both roles can read the screens; only the MD sees the forms.
        var mdPage = await (await md.GetAsync($"/Catalogue/Supplier/{supplierId}")).Content.ReadAsStringAsync();
        var estimatorPage = await estimator.GetAsync($"/Catalogue/Supplier/{supplierId}");
        var estimatorHtml = await estimatorPage.Content.ReadAsStringAsync();

        Assert.Contains("Add a product line", mdPage);

        // A size, then the colour's price screen, which lists the quotes using it.
        await PostFormAsync(md, "/Catalogue/AddSheetSize", new()
        {
            ["ProductLineId"] = lineId.ToString(), ["LengthMm"] = "3680", ["WidthMm"] = "760"
        });
        int colourId, sizeId;
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
            colourId = await db.Colours.Where(c => c.ProductLineId == lineId).Select(c => c.Id).SingleAsync();
            sizeId = await db.SheetSizes.Where(z => z.ProductLineId == lineId).Select(z => z.Id).SingleAsync();
        }
        var pricePage = await md.GetAsync($"/Catalogue/Price?colourId={colourId}&sheetSizeId={sizeId}");
        var priceHtml = await pricePage.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, pricePage.StatusCode);
        Assert.Contains("Quotes using this price", priceHtml);
        Assert.Contains("No price is in force today", priceHtml);
        Assert.Equal(HttpStatusCode.OK, estimatorPage.StatusCode);
        Assert.Contains("Arctic", estimatorHtml);
        Assert.Contains("Read only for you", estimatorHtml);
        Assert.DoesNotContain("Add a product line", estimatorHtml);
    }
}
