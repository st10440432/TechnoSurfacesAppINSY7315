using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Costing;

/// <summary>
/// The overrides, the transport amount and the quote's customer reference must
/// survive a save and a reload, or a quote reopened later would show different
/// figures from the ones the estimator saved.
/// </summary>
public sealed class CostingPersistenceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await RateCardSeeder.SeedAsync(db);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Overrides_and_transport_are_stored_with_the_version()
    {
        int quoteId;
        decimal totalWhenSaved;

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var customer = new Customer { Name = "RA Woodcraft" };
            db.Customers.Add(customer);
            await db.SaveChangesAsync();
            var contact = new Contact { CustomerId = customer.Id, FullName = "Contact" };
            db.Contacts.Add(contact);
            await db.SaveChangesAsync();

            var price = await db.MaterialPrices.FirstAsync(p => p.PriceBandId != null);
            var fabrication = await db.RateItems.FirstAsync(r => r.Name == "Fabrication — no backsplash, normal");
            var silicon = await db.RateItems.FirstAsync(r => r.Name == "Silicon + sealing");

            var quote = new Quote("TS-PERSIST-1", customer.Id, contact.Id, "estimator", new DateOnly(2026, 10, 2));
            quote.UpdateDetails(site: null, project: null, customerReference: "SWEET VALLEY FARM", deliveryAddress: null);
            var version = quote.StartNewVersion("estimator", markupPercent: 47m);

            var material = CostingLine.ForMaterial(price.Id, "Material", 4335.04m, "origin", 2m, 2.7968m);
            var labour = CostingLine.ForRate(fabrication.Id, fabrication.Name, 265m, "origin", 10m, isBelowTheLine: false);
            var siliconLine = CostingLine.ForRate(silicon.Id, silicon.Name, 55m, "origin", 0m, isBelowTheLine: false,
                DerivationRule.FromSheetCount, derivationFactor: 2m);
            version.AddCostingLine(material);
            version.AddCostingLine(labour);
            version.AddCostingLine(siliconLine);

            version.OverrideUnitPrice(labour, 300m);
            version.ChangeQuantity(siliconLine, 7m);
            version.ChangeSupplierDiscount(material, 5m);
            version.SetTransportAmount(850m);

            totalWhenSaved = new QuoteCalculationService().Calculate(version).TotalIncVat;

            db.Quotes.Add(quote);
            await db.SaveChangesAsync();
            quoteId = quote.Id;
        }

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await db.Quotes
                .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
                .SingleAsync(q => q.Id == quoteId);
            var version = quote.CurrentVersion!;

            var labour = version.CostingLines.Single(l => l.Description.StartsWith("Fabrication"));
            var silicon = version.CostingLines.Single(l => l.Description == "Silicon + sealing");
            var material = version.CostingLines.Single(l => l.LineType == CostingLineType.Material);

            Assert.Equal("SWEET VALLEY FARM", quote.CustomerReference);
            Assert.Equal(300m, labour.OverriddenUnitPrice);
            Assert.Equal(265m, labour.ResolvedUnitPrice);
            Assert.True(silicon.IsQuantityOverridden);
            Assert.Equal(7m, silicon.Quantity);
            Assert.Equal(5m, material.SupplierDiscountPercent);
            Assert.Equal(850m, version.TransportAmount);
            Assert.Equal(totalWhenSaved, new QuoteCalculationService().Calculate(version).TotalIncVat);
        }
    }
}
