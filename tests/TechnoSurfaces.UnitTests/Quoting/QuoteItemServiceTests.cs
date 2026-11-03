using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// Items through the costing sheet service on SQLite with the seeded catalogue and
/// rate card: added, renamed and removed, lines added to the item named, a markup
/// for one item, and the customer quotation following each item's own markup and
/// quantity.
/// </summary>
public sealed class QuoteItemServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _quoteId;
    private int _colourId;
    private int _sheetSizeId;
    private int _sandingId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await RateCardSeeder.SeedAsync(db);
        await QuotationTermsSeeder.SeedAsync(db);
        await CustomerSeeder.SeedAsync(db);

        var issued = new DateOnly(2026, 10, 4);
        var price = await db.MaterialPrices
            .Where(p => p.ColourId != null && p.EffectiveFrom <= issued && (p.EffectiveTo == null || p.EffectiveTo >= issued))
            .OrderBy(p => p.Id).FirstAsync();
        (_colourId, _sheetSizeId) = (price.ColourId!.Value, price.SheetSizeId);
        _sandingId = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var quote = new Quote("TS-ITEMS-1", customer.Id, customer.Contacts.Single().Id, "estimator", issued);
        quote.StartNewVersion("estimator", markupPercent: 50m);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        _quoteId = quote.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<T> WithCostingAsync<T>(Func<CostingSheetService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var catalogue = new CatalogueReader(db);
        var prices = new PriceResolver(catalogue, new IPriceResolutionStrategy[]
        {
            new BandPricedStrategy(catalogue),
            new ItemPricedStrategy(catalogue)
        });
        return await act(new CostingSheetService(new QuoteRepository(db), catalogue, prices, new RateResolver(catalogue),
            new QuoteCalculationService()));
    }

    private async Task<T> WithQuotationAsync<T>(Func<QuotationGenerationService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await act(new QuotationGenerationService(new QuoteRepository(db), new QuotationTermsReader(db), new QuoteCalculationService(),
            new MaterialNameReader(db)));
    }

    private Task<CostingResult> SheetAsync() => WithCostingAsync(s => s.GetAsync(_quoteId));

    private Task<CostingResult> AddMaterialAsync(int? itemId, decimal sheets = 1m) =>
        WithCostingAsync(s => s.AddMaterialLineAsync(_quoteId, new AddMaterialLine(_colourId, _sheetSizeId, sheets, 0m, itemId)));

    [Fact]
    public async Task A_new_quote_has_one_item_and_lines_go_to_it_when_none_is_named()
    {
        var added = await AddMaterialAsync(itemId: null);

        var sheet = await SheetAsync();
        var item = Assert.Single(sheet.Version!.Items);
        Assert.Equal(QuoteItem.FirstItemName, item.Name);
        Assert.Equal(item, sheet.Version.ItemOf(sheet.Version.CostingLines.Single(l => l.Id == added.Line!.Id)));
    }

    [Fact]
    public async Task An_item_is_added_saved_and_given_its_own_lines()
    {
        var added = await WithCostingAsync(s => s.AddItemAsync(_quoteId, new ItemInput("Floor unit, 4919 mm long", "4", 3m)));
        var vanityId = added.Item!.Id;

        await AddMaterialAsync(itemId: null);
        await AddMaterialAsync(vanityId, sheets: 2m);
        await WithCostingAsync(s => s.AddRateLineAsync(_quoteId, new AddRateLine(_sandingId, 2m, ItemId: vanityId)));

        var sheet = await SheetAsync();
        var floorUnit = sheet.Version!.Items.Single(i => i.Id == vanityId);
        Assert.Equal(("4", "Floor unit, 4919 mm long", 3m, 50m), (floorUnit.Number, floorUnit.Name, floorUnit.Quantity, floorUnit.MarkupPercent));
        Assert.Equal(2, sheet.Version.LinesOf(floorUnit).Count());
        Assert.Single(sheet.Version.LinesOf(sheet.Version.Items[0]));

        var figures = sheet.Totals!.Item(vanityId)!;
        Assert.Equal(figures.TotalExVat * 3m, figures.AmountExVat);
        Assert.Equal(sheet.Totals.Items.Sum(i => i.AmountExVat), sheet.Totals.TotalExVat);
    }

    [Fact]
    public async Task A_line_for_an_item_that_is_not_on_the_quote_is_refused()
    {
        var result = await AddMaterialAsync(itemId: 999);

        Assert.Equal(CostingOutcome.ItemNotFound, result.Outcome);
        Assert.Empty((await SheetAsync()).Version!.CostingLines);
    }

    [Fact]
    public async Task A_markup_for_one_item_leaves_the_others()
    {
        var second = (await WithCostingAsync(s => s.AddItemAsync(_quoteId, new ItemInput("Vanity", null, 1m)))).Item!.Id;

        await WithCostingAsync(s => s.ChangeCostingAsync(_quoteId, new ChangeCosting(MarkupPercent: 20m, ItemId: second)));

        var items = (await SheetAsync()).Version!.Items;
        Assert.Equal(new[] { 50m, 20m }, items.Select(i => i.MarkupPercent));
    }

    [Fact]
    public async Task An_item_is_renamed_and_its_quantity_changed()
    {
        var first = (await SheetAsync()).Version!.Items[0].Id;

        var result = await WithCostingAsync(s => s.ChangeItemAsync(_quoteId, first, new ItemInput("Kitchen", "1", 2m)));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        var item = (await SheetAsync()).Version!.Items[0];
        Assert.Equal(("1", "Kitchen", 2m), (item.Number, item.Name, item.Quantity));
    }

    [Fact]
    public async Task Removing_an_item_removes_its_lines_and_the_quotation_lines_copied_from_them()
    {
        var vanityId = (await WithCostingAsync(s => s.AddItemAsync(_quoteId, new ItemInput("Vanity", null, 1m)))).Item!.Id;
        await AddMaterialAsync(itemId: null);
        await AddMaterialAsync(vanityId);
        await WithQuotationAsync(s => s.AddLinesFromCostingAsync(_quoteId));

        var result = await WithCostingAsync(s => s.RemoveItemAsync(_quoteId, vanityId));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        var sheet = await SheetAsync();
        Assert.Single(sheet.Version!.Items);
        Assert.Single(sheet.Version.CostingLines);
        Assert.Single((await WithQuotationAsync(s => s.GenerateAsync(_quoteId)))!.Lines);
        Assert.True((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.Matches);
    }

    [Fact]
    public async Task The_last_item_cannot_be_removed()
    {
        var only = (await SheetAsync()).Version!.Items[0].Id;

        var result = await WithCostingAsync(s => s.RemoveItemAsync(_quoteId, only));

        Assert.Equal(CostingOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task A_quote_read_without_tracking_totals_each_item_by_its_own_markup()
    {
        // As the quote list and the dashboard read quotes: no tracking, so a line has its
        // item's id but not the item itself.
        var vanityId = (await WithCostingAsync(s => s.AddItemAsync(_quoteId, new ItemInput("Vanity", null, 2m, MarkupPercent: 10m)))).Item!.Id;
        await AddMaterialAsync(itemId: null);
        await AddMaterialAsync(vanityId);
        var tracked = (await SheetAsync()).Totals!;

        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await db.Quotes.AsNoTracking()
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .Include(q => q.Versions).ThenInclude(v => v.Items)
            .AsSplitQuery()
            .SingleAsync(q => q.Id == _quoteId);
        var read = new QuoteCalculationService().Calculate(quote.CurrentVersion!);

        Assert.Equal(tracked.TotalExVat, read.TotalExVat);
        Assert.Equal(tracked.Items.Select(i => i.AmountExVat), read.Items.Select(i => i.AmountExVat));
    }

    [Fact]
    public async Task Copied_quotation_lines_take_each_items_markup_and_quantity()
    {
        var vanityId = (await WithCostingAsync(s => s.AddItemAsync(_quoteId, new ItemInput("Vanity", null, 3m, MarkupPercent: 20m)))).Item!.Id;
        var kitchenLine = (await AddMaterialAsync(itemId: null)).Line!;
        var vanityLine = (await AddMaterialAsync(vanityId)).Line!;

        await WithQuotationAsync(s => s.AddLinesFromCostingAsync(_quoteId));

        // The kitchen at 50%; three vanities at 20%, carrying the cents lost to rounding
        // so the lines add up to the costing total exactly.
        var lines = (await WithQuotationAsync(s => s.GenerateAsync(_quoteId)))!.Lines;
        var total = (await SheetAsync()).Totals!.TotalExVat;
        Assert.Equal(decimal.Round(kitchenLine.LineTotal() * 1.5m, 2, MidpointRounding.AwayFromZero), lines[0].AmountExVat);
        Assert.Equal(total - lines[0].AmountExVat, lines[1].AmountExVat);
        Assert.InRange(lines[1].AmountExVat - vanityLine.LineTotal() * 1.2m * 3m, -0.05m, 0.05m);
        Assert.True((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.Matches);
    }

    [Fact]
    public async Task Buffer_stock_is_saved_for_a_material_on_the_quote_and_read_back_on_the_summary()
    {
        var line = (await AddMaterialAsync(itemId: null, sheets: 2m)).Line!;

        var saved = await WithCostingAsync(s => s.SetBufferStockAsync(_quoteId, line.Description, 3m));
        var unknown = await WithCostingAsync(s => s.SetBufferStockAsync(_quoteId, "Not a material on this quote", 3m));

        Assert.Equal(CostingOutcome.Ok, saved.Outcome);
        Assert.Equal(CostingOutcome.NotApplicable, unknown.Outcome);
        var material = Assert.Single((await WithCostingAsync(s => s.GetSheetSummaryAsync(_quoteId)))!.Materials);
        Assert.Equal((2m, 3m, 5m), (material.Sheets, material.BufferSheets, material.SheetsToOrder));

        await WithCostingAsync(s => s.SetBufferStockAsync(_quoteId, line.Description, 0m));
        Assert.Equal(0m, (await WithCostingAsync(s => s.GetSheetSummaryAsync(_quoteId)))!.Materials[0].BufferSheets);
    }

    [Fact]
    public async Task Copied_quotation_lines_of_an_item_priced_per_square_metre_add_up_to_its_price()
    {
        await AddMaterialAsync(itemId: null, sheets: 2m);
        await WithCostingAsync(s => s.AddRateLineAsync(_quoteId, new AddRateLine(_sandingId, 2m)));
        var first = (await SheetAsync()).Version!.Items[0].Id;
        await WithCostingAsync(s => s.ChangeItemAsync(_quoteId, first, new ItemInput("Kitchen", null, 1m, PricePerM2: 4500m)));

        await WithQuotationAsync(s => s.AddLinesFromCostingAsync(_quoteId));

        var sheet = await SheetAsync();
        var figures = sheet.Totals!.Items[0];
        Assert.Equal(decimal.Round(figures.AreaM2 * 4500m, 2, MidpointRounding.AwayFromZero), figures.TotalExVat);
        Assert.True((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.Matches);
    }
}
