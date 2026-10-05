using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Catalogue;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Catalogue;

/// <summary>
/// NFR-10: the catalogue is maintained by the Managing Director, add, edit and retire,
/// never delete. A material added here must then price and resolve like a seeded one,
/// under the supplier's own pricing scheme, and every change is audited.
/// </summary>
public sealed class CatalogueMaintenanceTests : IAsyncLifetime
{
    private const string ManagingDirectorId = "md-user-id";
    private static readonly DateOnly ListDate = new(2026, 9, 1);
    private static readonly DateOnly QuoteDate = new(2026, 10, 5);

    private sealed class FixedUser : ICurrentUser
    {
        public string UserId => ManagingDirectorId;
    }

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private CatalogueService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new AuditInterceptor(new FixedUser()))
                .Options);
        await _db.Database.EnsureCreatedAsync();
        _service = new CatalogueService(_db, new PriceHistory(_db), new FixedUser());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<PriceResolution> ResolveAsync(int colourId, int sheetSizeId)
    {
        var catalogue = new CatalogueReader(_db);
        var resolver = new PriceResolver(catalogue, new IPriceResolutionStrategy[]
        {
            new BandPricedStrategy(catalogue),
            new ItemPricedStrategy(catalogue)
        });
        return resolver.ResolveAsync(new PriceKey(colourId, sheetSizeId), QuoteDate);
    }

    private async Task<int> SucceedsAsync(Task<CatalogueResult> change)
    {
        var result = await change;
        Assert.True(result.Succeeded, result.Error);
        Assert.NotNull(result.Id);
        return result.Id!.Value;
    }

    private Task<int> AddSupplierAsync(string name, PricingStructure structure) =>
        SucceedsAsync(_service.AddSupplierAsync(new SupplierInput(name, null, structure, ListDate, 130m, "Quoted per order.")));

    [Fact]
    public async Task An_item_priced_colour_added_by_the_md_can_be_priced_and_resolves()
    {
        var supplier = await AddSupplierAsync("New item supplier", PricingStructure.Item);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));
        var size = await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        var colour = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Arctic", "AR-1", null, null)));

        // No price yet: it does not resolve, and is never zero.
        Assert.False((await ResolveAsync(colour, size)).Resolved);

        Assert.True((await _service.SetMaterialPriceAsync(colour, null, size, 1500.00m, ListDate)).Succeeded);
        var resolved = await ResolveAsync(colour, size);

        Assert.True(resolved.Resolved, resolved.FailureReason);
        Assert.Equal(decimal.Round(1500.00m * 3.68m * 0.76m, 2), resolved.UnitPrice);
    }

    [Fact]
    public async Task A_band_priced_colour_takes_its_price_from_its_band()
    {
        var supplier = await AddSupplierAsync("New band supplier", PricingStructure.Band);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 12, null));
        var size = await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        var band = await SucceedsAsync(_service.AddPriceBandAsync(line, "B2", "Band B2"));

        var noBand = await _service.AddColourAsync(line, new ColourInput("Cream", null, null, null));
        var colour = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Cream", null, null, band)));
        Assert.True((await _service.SetMaterialPriceAsync(null, band, size, 1683.00m, ListDate)).Succeeded);

        Assert.False(noBand.Succeeded);
        var resolved = await ResolveAsync(colour, size);
        Assert.True(resolved.Resolved, resolved.FailureReason);
        Assert.Contains("price band B2", resolved.Origin);
    }

    [Fact]
    public async Task An_item_priced_supplier_has_no_bands()
    {
        var supplier = await AddSupplierAsync("Item supplier", PricingStructure.Item);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));

        var band = await _service.AddPriceBandAsync(line, "A1", "A1");

        Assert.False(band.Succeeded);
        Assert.Empty(_db.PriceBands);
    }

    [Fact]
    public async Task A_colour_cannot_be_put_in_another_product_lines_band()
    {
        var supplier = await AddSupplierAsync("Band supplier", PricingStructure.Band);
        var thick = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 12, null));
        var thin = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 6, null));
        var thickBand = await SucceedsAsync(_service.AddPriceBandAsync(thick, "A1", "A1"));

        var result = await _service.AddColourAsync(thin, new ColourInput("White", null, null, thickBand));

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task Duplicates_are_refused_with_a_message()
    {
        var supplier = await AddSupplierAsync("Supplier", PricingStructure.Item);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));
        await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Arctic", null, null, null)));

        var results = new[]
        {
            await _service.AddSupplierAsync(new SupplierInput("Supplier", null, PricingStructure.Item, ListDate, 0m, null)),
            await _service.AddProductLineAsync(supplier, "Acrylic", 12, null),
            await _service.AddSheetSizeAsync(line, 3680, 760),
            await _service.AddColourAsync(line, new ColourInput("Arctic", null, null, null))
        };

        Assert.All(results, r => Assert.False(r.Succeeded));
        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.Error)));
    }

    [Fact]
    public async Task A_retired_product_line_takes_nothing_new_until_it_is_reinstated()
    {
        var supplier = await AddSupplierAsync("Supplier", PricingStructure.Item);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));
        var colour = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Arctic", null, null, null)));
        await _service.RetireColourAsync(colour);
        await _service.RetireProductLineAsync(line);

        var refusedColour = await _service.AddColourAsync(line, new ColourInput("Snow", null, null, null));
        var refusedReinstate = await _service.ReinstateColourAsync(colour);
        await SucceedsAsync(_service.ReinstateProductLineAsync(line));
        await SucceedsAsync(_service.ReinstateColourAsync(colour));

        Assert.False(refusedColour.Succeeded);
        Assert.False(refusedReinstate.Succeeded);
        Assert.Equal(CatalogueStatus.Active, (await _db.Colours.SingleAsync()).Status);
    }

    [Fact]
    public async Task How_a_supplier_prices_is_fixed_once_it_has_product_lines_but_its_list_date_can_move()
    {
        var supplier = await AddSupplierAsync("Supplier", PricingStructure.Item);
        await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));

        var switched = await _service.UpdateSupplierAsync(supplier,
            new SupplierInput("Supplier", null, PricingStructure.Band, ListDate, 130m, null));
        await SucceedsAsync(_service.UpdateSupplierAsync(supplier,
            new SupplierInput("Supplier", null, PricingStructure.Item, QuoteDate, 130m, null)));

        Assert.False(switched.Succeeded);
        var saved = await _db.Suppliers.SingleAsync();
        Assert.Equal(PricingStructure.Item, saved.PricingStructure);
        Assert.Equal(QuoteDate, saved.PriceListDated);
    }

    [Fact]
    public async Task Correcting_a_colour_changes_its_details_and_keeps_it_in_its_band()
    {
        var supplier = await AddSupplierAsync("Band supplier", PricingStructure.Band);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 12, null));
        var a1 = await SucceedsAsync(_service.AddPriceBandAsync(line, "A1", "A1"));
        var a2 = await SucceedsAsync(_service.AddPriceBandAsync(line, "A2", "A2"));
        var colour = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Whte", null, null, a1)));

        await SucceedsAsync(_service.UpdateColourAsync(colour, new ColourInput("White", "W-01", "Solids", a2)));

        var saved = await _db.Colours.AsNoTracking().SingleAsync();
        Assert.Equal("White", saved.Name);
        Assert.Equal("W-01", saved.SupplierCode);
        Assert.Equal(a2, saved.PriceBandId);
    }

    [Fact]
    public async Task Every_addition_is_recorded_in_the_audit_trail_against_the_md()
    {
        var supplier = await AddSupplierAsync("Band supplier", PricingStructure.Band);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 12, null));
        await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        var band = await SucceedsAsync(_service.AddPriceBandAsync(line, "A1", "A1"));
        await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("White", null, null, band)));

        var audited = await _db.AuditEntries.Select(a => new { a.EntityName, a.UserId }).Distinct().ToListAsync();

        foreach (var entity in new[] { nameof(Supplier), nameof(ProductLine), nameof(SheetSize), nameof(PriceBand), nameof(Colour) })
            Assert.Contains(audited, a => a.EntityName == entity && a.UserId == ManagingDirectorId);
    }

    [Fact]
    public async Task The_price_screen_lists_each_quote_with_the_price_it_locked_in()
    {
        var supplier = await AddSupplierAsync("Supplier", PricingStructure.Item);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Acrylic", 12, null));
        var size = await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        var colour = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Arctic", null, null, null)));
        var other = await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("Snow", null, null, null)));
        Assert.True((await _service.SetMaterialPriceAsync(colour, null, size, 1000.00m, ListDate)).Succeeded);
        Assert.True((await _service.SetMaterialPriceAsync(other, null, size, 900.00m, ListDate)).Succeeded);

        var customer = new TechnoSurfaces.Domain.People.Customer { Name = "Customer" };
        customer.Contacts.Add(new TechnoSurfaces.Domain.People.Contact { FullName = "Contact" });
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        async Task QuoteAsync(string reference, int colourId, DateOnly on)
        {
            var price = await _db.MaterialPrices.AsNoTracking()
                .Where(p => p.ColourId == colourId && p.EffectiveFrom <= on && (p.EffectiveTo == null || p.EffectiveTo >= on))
                .SingleAsync();
            var quote = new TechnoSurfaces.Domain.Quoting.Quote(reference, customer.Id, customer.Contacts.Single().Id, "estimator", on);
            quote.StartNewVersion("estimator", 40m).AddCostingLine(TechnoSurfaces.Domain.Quoting.CostingLine.ForMaterial(
                price.Id, "Acrylic Arctic 3680 x 760", price.PricePerSheet(new SheetSize { LengthMm = 3680, WidthMm = 760 }),
                "Supplier Arctic, 3680 x 760", 2m, 2.7968m));
            _db.Quotes.Add(quote);
            await _db.SaveChangesAsync();
        }

        await QuoteAsync("Q-OLD", colour, ListDate);
        await QuoteAsync("Q-OTHER", other, ListDate);
        Assert.True((await _service.SetMaterialPriceAsync(colour, null, size, 1200.00m, QuoteDate)).Succeeded);
        await QuoteAsync("Q-NEW", colour, QuoteDate);

        var quotes = await _service.GetQuotesUsingPriceAsync(colour, null, size);

        Assert.Equal(new[] { "Q-NEW", "Q-OLD" }, quotes.Select(q => q.Reference));
        Assert.Equal(decimal.Round(1200.00m * 2.7968m, 2), quotes[0].LockedUnitPrice);
        Assert.Equal(decimal.Round(1000.00m * 2.7968m, 2), quotes[1].LockedUnitPrice);
        Assert.Equal(ListDate, quotes[1].PriceFrom);
        Assert.All(quotes, q => Assert.True(q.IsCurrentVersion));
        Assert.Empty(await _service.GetQuotesUsingPriceAsync(colour, null, size + 999));
    }

    [Fact]
    public async Task The_supplier_screen_lists_its_lines_sizes_bands_and_colours()
    {
        var supplier = await AddSupplierAsync("Band supplier", PricingStructure.Band);
        var line = await SucceedsAsync(_service.AddProductLineAsync(supplier, "Solid", 12, null));
        await SucceedsAsync(_service.AddSheetSizeAsync(line, 3680, 760));
        var band = await SucceedsAsync(_service.AddPriceBandAsync(line, "A1", "Group A1"));
        await SucceedsAsync(_service.AddColourAsync(line, new ColourInput("White", null, null, band)));

        var detail = await _service.GetSupplierAsync(supplier);
        var row = Assert.Single(await _service.GetSuppliersAsync());

        var productLine = Assert.Single(detail!.ProductLines);
        Assert.True(detail.PricesByBand);
        Assert.Equal(3680, Assert.Single(productLine.SheetSizes).LengthMm);
        Assert.Equal("A1", Assert.Single(productLine.Bands).Code);
        Assert.Equal("A1", Assert.Single(productLine.Colours).Band);
        Assert.Equal(1, row.ProductLineCount);
        Assert.Equal(1, row.ColourCount);
        Assert.Null(await _service.GetSupplierAsync(9999));
    }
}
