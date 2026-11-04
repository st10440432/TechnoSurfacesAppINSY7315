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
/// The customer quotation as a bill of quantities, as the client sends a tender: each
/// item at its rate under its section, built from the costing so it always adds up to
/// it, with the transport and the allowance spread over the rates or listed on lines
/// of their own. On SQLite with the seeded rate card; each item's work is one line at
/// a price typed for the job, so the figures are easy to follow.
/// </summary>
public sealed class BillOfQuantitiesTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _quoteId;
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

        _sandingId = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var quote = new Quote("TS-BOQ-1", customer.Id, customer.Contacts.Single().Id, "estimator", new DateOnly(2026, 10, 4));
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

    private Task<CustomerQuotation?> DocumentAsync() => WithQuotationAsync(s => s.GenerateAsync(_quoteId));

    private async Task<int> FirstItemAsync() => (await WithCostingAsync(s => s.GetAsync(_quoteId))).Version!.Items[0].Id;

    private async Task<int> AddItemAsync(ItemInput item) =>
        (await WithCostingAsync(s => s.AddItemAsync(_quoteId, item))).Item!.Id;

    /// <summary>One line of work on the item at a price typed for this job, before the item's markup.</summary>
    private async Task CostAsync(int itemId, decimal cost)
    {
        var added = await WithCostingAsync(s => s.AddRateLineAsync(_quoteId, new AddRateLine(_sandingId, 1m, ItemId: itemId)));
        Assert.Equal(CostingOutcome.Ok, added.Outcome);
        var changed = await WithCostingAsync(s => s.ChangeLineAsync(_quoteId, added.Line!.Id, new ChangeLine(UnitPrice: cost)));
        Assert.Equal(CostingOutcome.Ok, changed.Outcome);
    }

    private Task<CostingResult> ExtrasAsync(decimal allowance, decimal transport = 0m) =>
        WithCostingAsync(s => s.ChangeCostingAsync(_quoteId, new ChangeCosting(TransportAmount: transport, AllowanceAmount: allowance)));

    private Task<QuotationResult> LayoutAsync(QuotationLayoutInput input) =>
        WithQuotationAsync(s => s.SetLayoutAsync(_quoteId, input));

    /// <summary>
    /// A reception counter, needed once, costing R1 000 (R1 500 at 50%), and a vanity
    /// needed three times, costing R200 each (R300 each, R900 for three).
    /// </summary>
    private async Task<(int Counter, int Vanity)> TwoItemsAsync()
    {
        var counter = await FirstItemAsync();
        await WithCostingAsync(s => s.ChangeItemAsync(_quoteId, counter, new ItemInput("Reception counter, 2400 mm", "1", 1m, Section: "Worktops")));
        var vanity = await AddItemAsync(new ItemInput("Vanity, 900 mm", "2", 3m, Section: "Floor units"));
        await CostAsync(counter, 1000m);
        await CostAsync(vanity, 200m);
        await LayoutAsync(new QuotationLayoutInput(Layout: "BillOfQuantities"));
        return (counter, vanity);
    }

    private static IEnumerable<CustomerBillItem> Rows(CustomerQuotation document) => document.Bill!.Sections.SelectMany(s => s.Items);

    [Fact]
    public async Task Each_item_is_a_row_at_its_rate_under_its_section()
    {
        await TwoItemsAsync();

        var document = (await DocumentAsync())!;

        Assert.Equal("BillOfQuantities", document.Layout);
        Assert.Empty(document.Lines);
        Assert.Equal(new[] { "Worktops", "Floor units" }, document.Bill!.Sections.Select(s => s.Heading));
        Assert.Equal(
            new[]
            {
                new CustomerBillItem("1", "Reception counter, 2400 mm", "No", 1m, 1500m, 1500m),
                new CustomerBillItem("2", "Vanity, 900 mm", "No", 3m, 300m, 900m)
            },
            Rows(document));
        Assert.Empty(document.Bill.Extras);
        Assert.Equal(2400m, document.Totals.SubtotalExVat);
    }

    [Fact]
    public async Task Items_next_to_each_other_under_one_heading_share_a_section()
    {
        await TwoItemsAsync();
        await AddItemAsync(new ItemInput("Vanity, 1200 mm", "3", 1m, Unit: "Set", Section: "Floor units"));
        await AddItemAsync(new ItemInput("Lockers", "4", 2m));

        var sections = (await DocumentAsync())!.Bill!.Sections;

        Assert.Equal(new string?[] { "Worktops", "Floor units", null }, sections.Select(s => s.Heading));
        Assert.Equal(new[] { "2", "3" }, sections[1].Items.Select(i => i.Number));
        Assert.Equal("Set", sections[1].Items[1].Unit);
    }

    [Fact]
    public async Task The_allowance_and_transport_are_spread_over_the_rates_in_proportion()
    {
        await TwoItemsAsync();
        await ExtrasAsync(allowance: 1000m, transport: 200m);

        // R1 200 on R2 400 of items: every rate carries half again.
        var document = (await DocumentAsync())!;

        Assert.Equal(new[] { (2250m, 2250m), (450m, 1350m) }, Rows(document).Select(r => (r.AmountEach, r.AmountExVat)));
        Assert.Empty(document.Bill!.Extras);
        Assert.Null(document.Bill.ExtrasHeading);
        Assert.Equal(3600m, document.Totals.SubtotalExVat);
        Assert.True((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.Matches);
    }

    [Fact]
    public async Task The_cents_lost_spreading_go_on_the_rate_of_an_item_needed_once()
    {
        await TwoItemsAsync();
        await ExtrasAsync(allowance: 101m);

        // R1 500 + R63,125 rounds to R1 563,13; R300 + R12,625 to R312,63, so three
        // vanities are R937,89. That is R2 501,02 on a total of R2 501,00: the counter,
        // needed once, gives back the two cents.
        var document = (await DocumentAsync())!;

        Assert.Equal(new[] { (1563.11m, 1563.11m), (312.63m, 937.89m) }, Rows(document).Select(r => (r.AmountEach, r.AmountExVat)));
        Assert.Equal(2501.00m, document.Totals.SubtotalExVat);
    }

    [Fact]
    public async Task With_no_item_needed_once_the_cents_go_on_the_last_amount()
    {
        var (counter, _) = await TwoItemsAsync();
        await WithCostingAsync(s => s.ChangeItemAsync(_quoteId, counter, new ItemInput("Reception counter, 2400 mm", "1", 2m, Section: "Worktops")));
        await ExtrasAsync(allowance: 101m);

        var document = (await DocumentAsync())!;
        var rows = Rows(document).ToList();

        Assert.Equal(4001.00m, document.Totals.SubtotalExVat);
        Assert.Equal((1538.85m, 3077.70m), (rows[0].AmountEach, rows[0].AmountExVat));
        Assert.Equal((307.77m, 923.30m), (rows[1].AmountEach, rows[1].AmountExVat));
    }

    [Fact]
    public async Task Not_spread_the_allowance_and_transport_are_listed_after_the_items()
    {
        await TwoItemsAsync();
        await ExtrasAsync(allowance: 1000m, transport: 200m);

        await LayoutAsync(new QuotationLayoutInput(SpreadAllowances: false));
        var document = (await DocumentAsync())!;

        Assert.Equal(new[] { 1500m, 300m }, Rows(document).Select(r => r.AmountEach));
        Assert.Equal(
            new[]
            {
                new CustomerBillExtra(QuoteVersion.DefaultAllowanceDescription, 1000m),
                new CustomerBillExtra("Transport", 200m)
            },
            document.Bill!.Extras);
        Assert.Equal("Allowances and transport", document.Bill.ExtrasHeading);
        Assert.Equal(3600m, document.Totals.SubtotalExVat);

        await LayoutAsync(new QuotationLayoutInput(AllowanceDescription: "Site inspection and travel to George"));
        Assert.Equal("Site inspection and travel to George", (await DocumentAsync())!.Bill!.Extras[0].Description);

        // Transport alone is headed as what it is.
        await ExtrasAsync(allowance: 0m, transport: 200m);
        Assert.Equal("Transport", (await DocumentAsync())!.Bill!.ExtrasHeading);
    }

    [Fact]
    public async Task A_bill_of_quantities_is_ready_to_approve_once_the_items_are_costed()
    {
        await LayoutAsync(new QuotationLayoutInput(Layout: "BillOfQuantities"));
        Assert.False((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.HasLines);

        await CostAsync(await FirstItemAsync(), 1000m);

        var check = (await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!;
        Assert.True(check.HasLines);
        Assert.True(check.Matches);
    }

    [Fact]
    public async Task On_written_lines_the_allowance_is_part_of_the_labour_and_extras_line()
    {
        await CostAsync(await FirstItemAsync(), 1000m);
        await ExtrasAsync(allowance: 500m);

        await WithQuotationAsync(s => s.AddLinesFromCostingAsync(_quoteId));

        var document = (await DocumentAsync())!;
        Assert.Null(document.Bill);
        Assert.Equal(2000m, Assert.Single(document.Lines).AmountExVat);
        Assert.True((await WithQuotationAsync(s => s.CheckAsync(_quoteId)))!.Matches);
    }

    [Fact]
    public async Task A_layout_that_does_not_exist_is_refused()
    {
        var result = await LayoutAsync(new QuotationLayoutInput(Layout: "Spreadsheet"));

        Assert.Equal(QuotationOutcome.Invalid, result.Outcome);
        Assert.Equal("Lines", (await DocumentAsync())!.Layout);
    }

    [Fact]
    public void The_allowance_is_added_at_cost_after_the_markup()
    {
        var version = new QuoteVersion(1, "estimator", markupPercent: 50m);
        version.AddCostingLine(CostingLine.ForRate(1, "Sanding time", 1000m, "Rate card", 1m, false, DerivationRule.Entered, 1m));

        version.SetAllowance(500m);

        Assert.Equal(500m, version.BelowTheLineTotal());
        Assert.Equal(2000m, version.TotalExVat());
        Assert.Throws<ArgumentOutOfRangeException>(() => version.SetAllowance(-1m));
    }

    [Fact]
    public void A_revision_keeps_the_layout_the_allowance_and_where_each_item_sits_on_the_bill()
    {
        var first = new QuoteVersion(1, "estimator", markupPercent: 50m);
        first.SetAllowance(1500m, "Travel to George");
        first.SetLayout(QuotationLayout.BillOfQuantities);
        first.SetSpreadAllowances(false);
        first.ChangeItem(first.Items[0], "Reception counter", "1", 2m, unit: "Set", section: "Worktops");

        var second = first.CreateRevision(2, "estimator");

        Assert.Equal((1500m, "Travel to George", QuotationLayout.BillOfQuantities, false),
            (second.AllowanceAmount, second.AllowanceWording, second.Layout, second.SpreadAllowances));
        Assert.Equal(("Set", "Worktops"), (second.Items[0].Unit, second.Items[0].Section));
    }
}
