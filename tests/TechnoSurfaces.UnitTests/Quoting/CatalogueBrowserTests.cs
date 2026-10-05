using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The cascading material choice (US-01, US-02) over the seeded catalogue. Each step
/// narrows the next, retired entries are left out (US-24), and an unknown parent is
/// distinguished from an empty list.
/// </summary>
public sealed class CatalogueBrowserTests : IAsyncLifetime
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
    public async Task Each_step_narrows_the_next()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var browser = new CatalogueBrowser(db);

        var suppliers = await browser.SuppliersAsync();
        Assert.Equal(5, suppliers.Count);

        var supplier = suppliers.First();
        var lines = await browser.ProductLinesAsync(supplier.Id);
        Assert.NotEmpty(lines!);
        Assert.All(lines!, l => Assert.True(db.ProductLines.Single(p => p.Id == l.Id).SupplierId == supplier.Id));

        var colours = await browser.ColoursAsync(lines![0].Id);
        Assert.NotEmpty(colours!);
        Assert.All(colours!, c => Assert.True(db.Colours.Single(x => x.Id == c.Id).ProductLineId == lines[0].Id));

        var sizes = await browser.SheetSizesAsync(colours![0].Id);
        Assert.NotEmpty(sizes!);
        Assert.All(sizes!, s => Assert.True(s.AreaM2 > 0m));
    }

    [Fact]
    public async Task Sheet_size_area_matches_the_domain_calculation()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var colour = await db.Colours.FirstAsync();
        var expected = (await db.SheetSizes.Where(s => s.ProductLineId == colour.ProductLineId).ToListAsync())
            .ToDictionary(s => s.Id, s => s.AreaM2);

        var sizes = await new CatalogueBrowser(db).SheetSizesAsync(colour.Id);

        Assert.All(sizes!, s => Assert.Equal(expected[s.Id], s.AreaM2));
    }

    [Fact]
    public async Task A_discontinued_colour_is_not_offered()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var colour = await db.Colours.FirstAsync();
        colour.Status = CatalogueStatus.Discontinued;
        await db.SaveChangesAsync();

        var colours = await new CatalogueBrowser(db).ColoursAsync(colour.ProductLineId);

        Assert.DoesNotContain(colours!, c => c.Id == colour.Id);
    }

    [Fact]
    public async Task An_unknown_parent_returns_null_not_an_empty_list()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var browser = new CatalogueBrowser(db);

        Assert.Null(await browser.ProductLinesAsync(999));
        Assert.Null(await browser.ColoursAsync(999));
        Assert.Null(await browser.SheetSizesAsync(999));
    }

    [Fact]
    public async Task Rate_items_follow_the_costing_sheet_order()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        var items = await new CatalogueBrowser(db).RateItemsAsync();

        Assert.Equal(await db.RateItems.CountAsync(), items.Count);
        Assert.Equal("Fabrication — no backsplash, normal", items[0].Name);
    }
}
