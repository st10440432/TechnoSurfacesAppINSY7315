using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The costing sheet against the real seeded catalogue and rate card. The first test
/// is the one the whole system rests on: a price that cannot be resolved adds no
/// line and saves nothing.
/// </summary>
public sealed class CostingSheetServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _quoteId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await RateCardSeeder.SeedAsync(db);

        var customer = new Customer { Name = "Test customer" };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var contact = new Contact { CustomerId = customer.Id, FullName = "Test contact" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        var quote = new Quote("TS-COST-1", customer.Id, contact.Id, "estimator", new DateOnly(2026, 10, 2));
        quote.StartNewVersion("estimator", markupPercent: 40m);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        _quoteId = quote.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private static CostingSheetService ServiceOver(TechnoSurfacesDbContext db)
    {
        var catalogue = new CatalogueReader(db);
        var prices = new PriceResolver(catalogue, new IPriceResolutionStrategy[]
        {
            new BandPricedStrategy(catalogue),
            new ItemPricedStrategy(catalogue)
        });
        return new CostingSheetService(new QuoteRepository(db), catalogue, prices, new RateResolver(catalogue), new QuoteCalculationService());
    }

    private async Task<int> LineCountAsync()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await db.CostingLines.CountAsync();
    }

    private async Task<int> AddPricedMaterialLineAsync(decimal quantity = 2m)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var price = await db.MaterialPrices.FirstAsync(p => p.ColourId != null);
        var result = await ServiceOver(db).AddMaterialLineAsync(_quoteId,
            new AddMaterialLine(price.ColourId!.Value, price.SheetSizeId, quantity, 0m));
        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        return result.Line!.Id;
    }

    private async Task<int> AddRateLineAsync(string name, decimal quantity)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var item = await db.RateItems.SingleAsync(r => r.Name == name);
        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(item.Id, quantity));
        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        return result.Line!.Id;
    }

    [Fact]
    public async Task An_unpriced_rate_adds_no_line_and_is_never_zero()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var unpriced = await db.RateItems.SingleAsync(r => r.Name == "Sink / vanity");

        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(unpriced.Id, 1m));

        Assert.Equal(CostingOutcome.PriceNotResolved, result.Outcome);
        Assert.False(string.IsNullOrWhiteSpace(result.Problem));
        Assert.Null(result.Line);
        Assert.Equal(0, await LineCountAsync());
    }

    [Fact]
    public async Task An_unpriced_rate_takes_a_price_typed_for_the_job()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var board = await db.RateItems.SingleAsync(r => r.Name == "Marine Ply 18mm");

        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(board.Id, 2m, UnitPrice: 950m));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        Assert.Equal(950m, result.Line!.ResolvedUnitPrice);
        Assert.Equal(CostingSheetService.EnteredOnQuoteOrigin, result.Line.PriceOrigin);
        Assert.Equal(1900m, result.Totals!.SubTotalExVat);
        Assert.Equal(1, await LineCountAsync());
    }

    [Fact]
    public async Task A_typed_price_is_refused_for_an_item_with_a_rate_card_price()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var sanding = await db.RateItems.SingleAsync(r => r.Name == "Sanding time");

        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(sanding.Id, 2m, UnitPrice: 80m));

        Assert.Equal(CostingOutcome.NotApplicable, result.Outcome);
        Assert.Contains("R100.00", result.Problem);
        Assert.Equal(0, await LineCountAsync());
    }

    [Fact]
    public async Task An_unpriced_rate_without_a_typed_price_says_how_to_add_it()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var sink = await db.RateItems.SingleAsync(r => r.Name == "Sink / vanity");

        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(sink.Id, 1m));

        Assert.Equal(CostingOutcome.PriceNotResolved, result.Outcome);
        Assert.Contains("enter a price for this job", result.Problem);
    }

    [Fact]
    public async Task A_colour_with_no_price_at_that_size_adds_no_line()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var colour = await db.Colours.Include(c => c.ProductLine).FirstAsync();
        var sizeFromAnotherLine = await db.SheetSizes.FirstAsync(s => s.ProductLineId != colour.ProductLineId);

        var result = await ServiceOver(db).AddMaterialLineAsync(_quoteId,
            new AddMaterialLine(colour.Id, sizeFromAnotherLine.Id, 1m, 0m));

        Assert.Equal(CostingOutcome.PriceNotResolved, result.Outcome);
        Assert.Equal(0, await LineCountAsync());
    }

    [Fact]
    public async Task A_resolved_material_line_records_its_price_row_and_origin()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var price = await db.MaterialPrices.FirstAsync(p => p.ColourId != null);

        var result = await ServiceOver(db).AddMaterialLineAsync(_quoteId,
            new AddMaterialLine(price.ColourId!.Value, price.SheetSizeId, 2m, 0m));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        Assert.Equal(price.Id, result.Line!.MaterialPriceId);
        Assert.True(result.Line.ResolvedUnitPrice > 0m);
        Assert.NotEqual(PriceResolution.UnresolvedOrigin, result.Line.PriceOrigin);
        Assert.Equal(result.Line.LineTotal(), result.Totals!.SubTotalExVat);
        Assert.Equal(1, await LineCountAsync());
    }

    [Fact]
    public async Task A_band_priced_material_line_records_the_band_price_row()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var bandPrice = await db.MaterialPrices.FirstAsync(p => p.PriceBandId != null);
        var colour = await db.Colours.FirstAsync(c => c.PriceBandId == bandPrice.PriceBandId);

        var result = await ServiceOver(db).AddMaterialLineAsync(_quoteId,
            new AddMaterialLine(colour.Id, bandPrice.SheetSizeId, 1m, 0m));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        Assert.Equal(bandPrice.Id, result.Line!.MaterialPriceId);
    }

    [Fact]
    public async Task A_discontinued_colour_cannot_be_added()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var price = await db.MaterialPrices.FirstAsync(p => p.ColourId != null);
        var colour = await db.Colours.SingleAsync(c => c.Id == price.ColourId);
        colour.Status = CatalogueStatus.Discontinued;
        await db.SaveChangesAsync();

        var result = await ServiceOver(db).AddMaterialLineAsync(_quoteId,
            new AddMaterialLine(colour.Id, price.SheetSizeId, 1m, 0m));

        Assert.Equal(CostingOutcome.NotSelectable, result.Outcome);
        Assert.Equal(0, await LineCountAsync());
    }

    [Fact]
    public async Task A_sealed_version_takes_no_new_line()
    {
        await using (var setup = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(setup).GetAsync(_quoteId);
            quote!.Submit();
            quote.Approve("md");
            await setup.SaveChangesAsync();
        }

        await using var db = new TechnoSurfacesDbContext(_options);
        var fabrication = await db.RateItems.SingleAsync(r => r.Name == "Fabrication — no backsplash, normal");

        var result = await ServiceOver(db).AddRateLineAsync(_quoteId, new AddRateLine(fabrication.Id, 2m));

        Assert.Equal(CostingOutcome.VersionSealed, result.Outcome);
        Assert.Equal(0, await LineCountAsync());
    }

    [Fact]
    public async Task Changing_a_line_overrides_its_rate_on_this_quote_and_saves_it()
    {
        var lineId = await AddRateLineAsync("Fabrication — no backsplash, normal", 4m);

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var result = await ServiceOver(db).ChangeLineAsync(_quoteId, lineId,
                new ChangeLine(Quantity: 6m, UnitPrice: 300m));

            Assert.Equal(CostingOutcome.Ok, result.Outcome);
            Assert.Equal(1800m, result.Line!.LineTotal());
            Assert.Equal(1800m, result.Totals!.SubTotalExVat);
        }

        await using var reread = new TechnoSurfacesDbContext(_options);
        var line = await reread.CostingLines.SingleAsync(l => l.Id == lineId);
        Assert.Equal(6m, line.Quantity);
        Assert.Equal(300m, line.OverriddenUnitPrice);
        Assert.Equal(265m, line.ResolvedUnitPrice);
    }

    [Fact]
    public async Task A_supplier_discount_on_a_rate_line_is_refused()
    {
        var lineId = await AddRateLineAsync("Fabrication — no backsplash, normal", 4m);

        await using var db = new TechnoSurfacesDbContext(_options);
        var result = await ServiceOver(db).ChangeLineAsync(_quoteId, lineId,
            new ChangeLine(SupplierDiscountPercent: 10m));

        Assert.Equal(CostingOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task Changing_a_line_that_is_not_on_the_quote_is_not_found()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var result = await ServiceOver(db).ChangeLineAsync(_quoteId, 999, new ChangeLine(Quantity: 1m));

        Assert.Equal(CostingOutcome.LineNotFound, result.Outcome);
    }

    [Fact]
    public async Task Removing_a_line_deletes_it_and_recalculates()
    {
        var keep = await AddPricedMaterialLineAsync();
        var remove = await AddRateLineAsync("Sanding time", 2m);

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var result = await ServiceOver(db).RemoveLineAsync(_quoteId, remove);

            Assert.Equal(CostingOutcome.Ok, result.Outcome);
            var remaining = await db.CostingLines.SingleAsync(l => l.Id == keep);
            Assert.Equal(remaining.LineTotal(), result.Totals!.SubTotalExVat);
        }

        Assert.Equal(1, await LineCountAsync());
    }

    [Fact]
    public async Task Markup_and_transport_are_set_on_the_open_version()
    {
        await AddPricedMaterialLineAsync();

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var result = await ServiceOver(db).ChangeCostingAsync(_quoteId,
                new ChangeCosting(MarkupPercent: 47m, TransportAmount: 850m));

            Assert.Equal(CostingOutcome.Ok, result.Outcome);
            Assert.Equal(47m, result.Totals!.MarkupPercent);
            Assert.Equal(850m, result.Totals.TransportAmount);
            Assert.Equal(
                result.Totals.SubTotalExVat + result.Totals.MarkupAmount + result.Totals.BelowTheLineTotal,
                result.Totals.TotalExVat);
        }

        await using var reread = new TechnoSurfacesDbContext(_options);
        var version = await reread.QuoteVersions.SingleAsync(v => v.QuoteId == _quoteId);
        Assert.Equal(47m, version.MarkupPercent);
        Assert.Equal(850m, version.TransportAmount);
    }

    [Fact]
    public async Task An_unknown_quote_is_not_found()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var service = ServiceOver(db);

        Assert.Equal(CostingOutcome.QuoteNotFound, (await service.GetAsync(999)).Outcome);
        Assert.Equal(CostingOutcome.QuoteNotFound,
            (await service.AddRateLineAsync(999, new AddRateLine(1, 1m))).Outcome);
    }

    [Fact]
    public async Task An_earlier_version_reads_with_its_own_lines_after_the_quote_is_revised()
    {
        await AddPricedMaterialLineAsync(quantity: 2m);

        // Issue version 1, then reopen it and change the revision.
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            quote!.CurrentVersion!.AddQuotationLine(new QuotationLine("Kitchen", 1000m));
            quote.Submit();
            quote.Approve("md");
            quote.MarkSent();
            quote.Reopen("estimator", new DateOnly(2026, 10, 20));
            await db.SaveChangesAsync();
        }
        var revisionLine = await AddPricedMaterialLineAsync(quantity: 5m);

        await using var read = new TechnoSurfacesDbContext(_options);
        var first = await ServiceOver(read).GetVersionAsync(_quoteId, 1);
        var second = await ServiceOver(read).GetVersionAsync(_quoteId, 2);

        Assert.Equal(CostingOutcome.Ok, first.Outcome);
        Assert.Equal(1, first.Version!.VersionNo);
        Assert.True(first.Version.IsSealed);
        Assert.Equal(2m, first.Totals!.TotalSheetCount);
        Assert.DoesNotContain(first.Version.CostingLines, l => l.Id == revisionLine);

        Assert.Equal(2, second.Version!.VersionNo);
        Assert.Equal(7m, second.Totals!.TotalSheetCount);
        Assert.True(second.Totals.TotalExVat > first.Totals.TotalExVat);
    }

    [Fact]
    public async Task A_version_the_quote_does_not_have_is_not_found()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        var missingVersion = await ServiceOver(db).GetVersionAsync(_quoteId, 2);
        var missingQuote = await ServiceOver(db).GetVersionAsync(9999, 1);

        Assert.Equal(CostingOutcome.VersionNotFound, missingVersion.Outcome);
        Assert.Equal(CostingOutcome.QuoteNotFound, missingQuote.Outcome);
    }
}
