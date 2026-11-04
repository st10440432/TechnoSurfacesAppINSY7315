using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// Lines copied from the costing sheet onto the customer quotation stay in step with
/// it: copying again adds only what is new, a change on the costing sheet changes
/// the copied line, and the sheet count is shown only when asked for. On SQLite with
/// the seeded catalogue and rate card.
///
/// The fixture costs one Infinito sheet at R1 000 and two hours of sanding at R100,
/// with a 50% markup: R1 800 ex VAT, of which the material sells at R1 500.
/// </summary>
public sealed class QuotationFollowsCostingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _quoteId;
    private int _materialPriceId;
    private string _materialName = "";

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

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var price = await db.MaterialPrices
            .Include(p => p.PriceBand).ThenInclude(b => b!.ProductLine)
            .FirstAsync(p => p.PriceBand != null && p.PriceBand.ProductLine!.Name.StartsWith("Infinito"));
        _materialPriceId = price.Id;
        _materialName = $"{price.PriceBand!.ProductLine!.Name} {price.PriceBand.ProductLine.ThicknessMm} mm";

        var quote = new Quote("TS-FOLLOW-1", customer.Id, customer.Contacts.Single().Id, "estimator", new DateOnly(2026, 10, 4));
        var version = quote.StartNewVersion("estimator", markupPercent: 50m);
        version.AddCostingLine(Material(1000m, sheets: 1m));
        version.AddCostingLine(CostingLine.ForRate(1, "Sanding time", 100m, "Rate card: Sanding time", 2m, isBelowTheLine: false));
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        _quoteId = quote.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private CostingLine Material(decimal sheetPrice, decimal sheets) =>
        CostingLine.ForMaterial(_materialPriceId, "Infinito material line", sheetPrice, "Price band", sheets, 2.7968m);

    private async Task<T> WithQuotationAsync<T>(Func<QuotationGenerationService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await act(new QuotationGenerationService(new QuoteRepository(db), new QuotationTermsReader(db), new QuoteCalculationService(),
            new MaterialNameReader(db)));
    }

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

    private Task<CustomerQuotation?> DocumentAsync() => WithQuotationAsync(s => s.GenerateAsync(_quoteId));

    private Task<QuotationCheck?> CheckAsync() => WithQuotationAsync(s => s.CheckAsync(_quoteId));

    private async Task<int> CostingLineIdAsync(CostingLineType type)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await db.CostingLines
            .Where(l => l.LineType == type && db.QuoteVersions.Any(v => v.Id == l.QuoteVersionId && v.QuoteId == _quoteId))
            .OrderByDescending(l => l.QuoteVersionId).ThenBy(l => l.Id)
            .Select(l => l.Id)
            .FirstAsync();
    }

    private Task CopyAsync() => WithQuotationAsync(s => s.AddLinesFromCostingAsync(_quoteId));

    [Fact]
    public async Task Copying_twice_adds_nothing_the_second_time()
    {
        await CopyAsync();
        await CopyAsync();

        var document = await DocumentAsync();
        Assert.Equal(new[] { _materialName, QuotationGenerationService.OtherCostsDescription }, document!.Lines.Select(l => l.Description));
        Assert.Equal(new[] { 1500m, 300m }, document.Lines.Select(l => l.AmountExVat));
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task Copying_again_adds_only_the_material_that_is_new()
    {
        await CopyAsync();
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            quote!.CurrentVersion!.AddCostingLine(Material(500m, sheets: 1m));
            await db.SaveChangesAsync();
        }

        await CopyAsync();

        var document = await DocumentAsync();
        Assert.Equal(new[] { 1500m, 300m, 750m }, document!.Lines.Select(l => l.AmountExVat));
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task A_new_sheet_count_on_the_costing_changes_the_copied_line_at_once()
    {
        await CopyAsync();
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2m)));

        // R2 000 + R200 sanding, marked up 50%: R3 000 for the material, R300 for the rest.
        var document = await DocumentAsync();
        Assert.Equal(new[] { 3000m, 300m }, document!.Lines.Select(l => l.AmountExVat));
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task A_new_price_or_discount_on_the_costing_changes_the_copied_line()
    {
        await CopyAsync();
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(UnitPrice: 1200m, SupplierDiscountPercent: 10m)));

        // R1 200 less 10% is R1 080, marked up 50%.
        Assert.Equal(1620m, (await DocumentAsync())!.Lines[0].AmountExVat);
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task A_new_markup_changes_every_copied_line()
    {
        await CopyAsync();

        await WithCostingAsync(s => s.ChangeCostingAsync(_quoteId, new ChangeCosting(MarkupPercent: 20m)));

        Assert.Equal(new[] { 1200m, 240m }, (await DocumentAsync())!.Lines.Select(l => l.AmountExVat));
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task More_labour_on_the_costing_changes_the_line_for_the_rest()
    {
        await CopyAsync();
        var sandingId = await CostingLineIdAsync(CostingLineType.Rate);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, sandingId, new ChangeLine(Quantity: 4m)));

        Assert.Equal(new[] { 1500m, 600m }, (await DocumentAsync())!.Lines.Select(l => l.AmountExVat));
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task A_material_taken_off_the_costing_is_taken_off_the_quotation()
    {
        await CopyAsync();
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        var result = await WithCostingAsync(s => s.RemoveLineAsync(_quoteId, materialId));

        Assert.Equal(CostingOutcome.Ok, result.Outcome);
        var line = Assert.Single((await DocumentAsync())!.Lines);
        Assert.Equal(QuotationGenerationService.OtherCostsDescription, line.Description);
        Assert.Equal(300m, line.AmountExVat);
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task With_no_labour_left_the_line_for_the_rest_goes()
    {
        await CopyAsync();
        var sandingId = await CostingLineIdAsync(CostingLineType.Rate);

        await WithCostingAsync(s => s.RemoveLineAsync(_quoteId, sandingId));

        var line = Assert.Single((await DocumentAsync())!.Lines);
        Assert.Equal(_materialName, line.Description);
        Assert.True((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task A_reworded_copied_line_keeps_its_words_and_takes_the_new_amount()
    {
        await CopyAsync();
        var lineId = (await DocumentAsync())!.Lines[0].Id;
        await WithQuotationAsync(s => s.ChangeLineAsync(_quoteId, lineId, new QuotationLineInput("Kitchen top in Infinito", 1500m, "Kitchen")));
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2m)));

        var line = (await DocumentAsync())!.Lines[0];
        Assert.Equal(("Kitchen", "Kitchen top in Infinito", 3000m), (line.Room, line.Description, line.AmountExVat));
    }

    [Fact]
    public async Task A_line_written_by_hand_is_never_changed_by_the_costing()
    {
        await WithQuotationAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Site measure", 250m, "Kitchen")));
        await CopyAsync();
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2m)));
        await CopyAsync();

        var document = await DocumentAsync();
        Assert.Equal(new[] { "Site measure", _materialName, QuotationGenerationService.OtherCostsDescription },
            document!.Lines.Select(l => l.Description));
        Assert.Equal(250m, document.Lines[0].AmountExVat);
    }

    [Fact]
    public async Task A_new_material_on_the_costing_waits_until_it_is_copied()
    {
        await CopyAsync();

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            quote!.CurrentVersion!.AddCostingLine(Material(500m, sheets: 1m));
            await db.SaveChangesAsync();
        }

        Assert.Equal(2, (await DocumentAsync())!.Lines.Count);
        Assert.False((await CheckAsync())!.Matches);
    }

    [Fact]
    public async Task Sheet_counts_are_off_until_asked_for_and_follow_the_costing()
    {
        var materialId = await CostingLineIdAsync(CostingLineType.Material);
        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 3m)));
        await CopyAsync();
        Assert.Equal(1m, (await DocumentAsync())!.Lines[0].Quantity);

        await WithQuotationAsync(s => s.SetShowSheetCountsAsync(_quoteId, true));
        Assert.Equal(3m, (await DocumentAsync())!.Lines[0].Quantity);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 4m)));
        var lines = (await DocumentAsync())!.Lines;
        Assert.Equal((4m, 6000m), (lines[0].Quantity, lines[0].AmountExVat));
        Assert.Equal(1m, lines[1].Quantity);

        await WithQuotationAsync(s => s.SetShowSheetCountsAsync(_quoteId, false));
        Assert.Equal(1m, (await DocumentAsync())!.Lines[0].Quantity);
    }

    [Fact]
    public async Task Sheet_counts_apply_to_materials_copied_while_they_are_on()
    {
        var materialId = await CostingLineIdAsync(CostingLineType.Material);
        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2.5m)));
        await WithQuotationAsync(s => s.SetShowSheetCountsAsync(_quoteId, true));

        await CopyAsync();

        Assert.Equal(2.5m, (await DocumentAsync())!.Lines[0].Quantity);
    }

    [Fact]
    public async Task Lines_copied_before_they_remembered_their_material_are_not_copied_twice()
    {
        // As a quote copied before this change was made has them: right item and
        // description, but no link to the costing.
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            var version = quote!.CurrentVersion!;
            version.AddQuotationLine(new QuotationLine(_materialName, 1500m, QuotationGenerationService.MaterialItem) { SortOrder = 1 });
            version.AddQuotationLine(new QuotationLine(QuotationGenerationService.OtherCostsDescription, 300m,
                QuotationGenerationService.OtherCostsItem) { SortOrder = 2 });
            await db.SaveChangesAsync();
        }

        await CopyAsync();
        var materialId = await CostingLineIdAsync(CostingLineType.Material);
        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2m)));

        Assert.Equal(new[] { 3000m, 300m }, (await DocumentAsync())!.Lines.Select(l => l.AmountExVat));
    }

    [Fact]
    public async Task A_sealed_version_is_not_changed_by_its_revision()
    {
        await CopyAsync();
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            quote!.Submit();
            await new QuoteTermsRecorder(new QuotationTermsReader(db)).RecordAsync(quote.CurrentVersion!);
            quote.Approve("md");
            quote.MarkSent();
            quote.Reopen("estimator", new DateOnly(2026, 10, 20));
            await db.SaveChangesAsync();
        }
        var materialId = await CostingLineIdAsync(CostingLineType.Material);

        await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, materialId, new ChangeLine(Quantity: 2m)));
        await CopyAsync();

        var first = await WithQuotationAsync(s => s.GenerateVersionAsync(_quoteId, 1));
        var second = await WithQuotationAsync(s => s.GenerateVersionAsync(_quoteId, 2));
        Assert.Equal(new[] { 1500m, 300m }, first!.Lines.Select(l => l.AmountExVat));
        Assert.Equal(new[] { 3000m, 300m }, second!.Lines.Select(l => l.AmountExVat));
    }
}
