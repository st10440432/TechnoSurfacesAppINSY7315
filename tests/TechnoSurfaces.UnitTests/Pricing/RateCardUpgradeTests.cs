using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Pricing;

/// <summary>
/// A database seeded before the rate card followed the client's workbook holds an
/// earlier card with no sort order. The seeder replaces it, but never once a quote
/// depends on it.
/// </summary>
public sealed class RateCardUpgradeTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<RateItem> AddEarlierCardAsync()
    {
        var fabrication = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        _db.RateItems.Add(fabrication);
        await _db.SaveChangesAsync();

        var overtime = new RateItem
        {
            Name = "Fabrication overtime",
            Category = RateCategory.Fabrication,
            Unit = ChargeUnit.Hour,
            DerivedFromRateItemId = fabrication.Id,
            DerivedFromRateItemMultiplier = 1.5m
        };
        _db.RateItems.Add(overtime);
        _db.RatePrices.Add(new RatePrice { RateItemId = fabrication.Id, Amount = 1050m, EffectiveFrom = new DateOnly(2026, 1, 1) });
        await _db.SaveChangesAsync();

        return fabrication;
    }

    [Fact]
    public async Task An_earlier_unordered_card_is_replaced_by_the_workbook_card()
    {
        await AddEarlierCardAsync();

        await RateCardSeeder.SeedAsync(_db);

        Assert.False(await _db.RateItems.AnyAsync(r => r.Name == "Fabrication"));
        Assert.Equal(33, await _db.RateItems.CountAsync());
        Assert.False(await _db.RatePrices.AnyAsync(p => p.Amount == 1050m));
    }

    [Fact]
    public async Task An_earlier_card_used_by_a_quote_is_left_alone()
    {
        var fabrication = await AddEarlierCardAsync();

        var quote = new Quote("TS-TEST-1", customerId: await AddCustomerAsync(), contactId: _contactId,
            createdByUserId: "user", issueDate: new DateOnly(2026, 9, 1));
        var version = quote.StartNewVersion("user", markupPercent: 0m);
        version.AddCostingLine(CostingLine.ForRate(fabrication.Id, "Fabrication", 300m, "Rate card", 1m, isBelowTheLine: false));
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync();

        await RateCardSeeder.SeedAsync(_db);

        Assert.True(await _db.RateItems.AnyAsync(r => r.Name == "Fabrication"));
        Assert.Equal(2, await _db.RateItems.CountAsync());
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_the_card()
    {
        await RateCardSeeder.SeedAsync(_db);
        await RateCardSeeder.SeedAsync(_db);

        Assert.Equal(33, await _db.RateItems.CountAsync());
    }

    private int _contactId;

    private async Task<int> AddCustomerAsync()
    {
        var customer = new TechnoSurfaces.Domain.People.Customer { Name = "Test customer" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var contact = new TechnoSurfaces.Domain.People.Contact { CustomerId = customer.Id, FullName = "Test contact" };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        _contactId = contact.Id;
        return customer.Id;
    }
}
