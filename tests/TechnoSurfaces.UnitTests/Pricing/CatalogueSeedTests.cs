using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Pricing;

/// <summary>
/// Loads the real catalogue from the client's supplier price lists and resolves
/// prices against it. This proves the model accommodates all five suppliers, three
/// of whom price by band and two by item, and that the published figures come back
/// to the cent.
/// </summary>
public sealed class CatalogueSeedTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private IPriceResolver _resolver = null!;

    private static readonly DateOnly Today = new(2026, 9, 29);

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>()
            .UseSqlite(_connection)
            .Options;

        _db = new TechnoSurfacesDbContext(options);
        await _db.Database.EnsureCreatedAsync();

        await CatalogueSeeder.SeedAsync(_db);
        await RateCardSeeder.SeedAsync(_db);

        var catalogue = new CatalogueReader(_db);
        _resolver = new PriceResolver(catalogue, new IPriceResolutionStrategy[]
        {
            new BandPricedStrategy(catalogue),
            new ItemPricedStrategy(catalogue)
        });
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task All_five_suppliers_are_seeded_and_both_pricing_schemes_are_represented()
    {
        var suppliers = await _db.Suppliers.AsNoTracking().ToListAsync();

        Assert.Equal(5, suppliers.Count);
        Assert.Equal(2, suppliers.Count(s => s.PricingStructure == PricingStructure.Band));
        Assert.Equal(3, suppliers.Count(s => s.PricingStructure == PricingStructure.Item));
    }

    [Fact]
    public async Task Every_seeded_colour_resolves_to_a_price()
    {
        // Nothing in the catalogue may be unpriceable. A colour the system cannot
        // price is the failure this project exists to make visible, so it must not
        // be shipped in the seed data.
        var colours = await _db.Colours
            .AsNoTracking()
            .Select(c => new { c.Id, c.Name, c.ProductLineId })
            .ToListAsync();

        Assert.NotEmpty(colours);

        var unresolved = new List<string>();

        foreach (var colour in colours)
        {
            var sizes = await _db.SheetSizes
                .AsNoTracking()
                .Where(s => s.ProductLineId == colour.ProductLineId)
                .Select(s => s.Id)
                .ToListAsync();

            var anyResolved = false;
            foreach (var sizeId in sizes)
            {
                var result = await _resolver.ResolveAsync(new PriceKey(colour.Id, sizeId), Today);
                if (result.Resolved) { anyResolved = true; break; }
            }

            if (!anyResolved) unresolved.Add(colour.Name);
        }

        Assert.True(unresolved.Count == 0,
            "These seeded colours cannot be priced: " + string.Join(", ", unresolved));
    }

    [Theory]
    [InlineData("Woodcentre CPT")]
    public async Task Every_item_priced_sheet_resolves_to_the_whole_rand_figure_on_the_list(string supplierName)
    {
        // Every sheet price on the Woodcentre list is a whole rand amount. Woodcentre
        // publishes only a sheet price, so the per-square-metre figure is derived from
        // it; if that derivation loses precision the sheet price comes back a cent or
        // two out. Max on Top is not here: its list prices the square metre, and its
        // sheet price is that times the area, so it is not a whole rand amount.
        var supplier = await _db.Suppliers.AsNoTracking().FirstAsync(s => s.Name == supplierName);

        var rows = await _db.Colours
            .AsNoTracking()
            .Where(c => c.ProductLine!.SupplierId == supplier.Id)
            .Select(c => new { c.Id, c.Name, c.ProductLineId })
            .ToListAsync();

        Assert.NotEmpty(rows);

        var drifted = new List<string>();

        foreach (var row in rows)
        {
            var sizeIds = await _db.SheetSizes
                .AsNoTracking()
                .Where(s => s.ProductLineId == row.ProductLineId)
                .Select(s => s.Id)
                .ToListAsync();

            foreach (var sizeId in sizeIds)
            {
                var result = await _resolver.ResolveAsync(new PriceKey(row.Id, sizeId), Today);
                if (!result.Resolved) continue;

                if (result.UnitPrice != decimal.Truncate(result.UnitPrice))
                    drifted.Add($"{row.Name} resolved to {result.UnitPrice}");
            }
        }

        Assert.True(drifted.Count == 0,
            "These prices did not round back to the published whole-rand figure: " + string.Join("; ", drifted));
    }

    [Theory]
    [InlineData("Alaskan Stone 4312", 8292.98)]      // R2 983 per m2 on a 3658 x 760 sheet
    [InlineData("Arctica 9015", 4670.66)]            // R1 670 per m2 on a 3680 x 760 sheet
    [InlineData("GetaCore Glacier White GC2011", 3901.50)] // R1 530 per m2 on a 2040 x 1250 sheet
    public async Task A_max_on_top_sheet_price_is_the_list_price_per_square_metre_times_the_area(string colourName, double sheetPrice)
    {
        var colour = await _db.Colours.AsNoTracking().FirstAsync(c => c.Name == colourName);
        var size = await _db.SheetSizes.AsNoTracking().FirstAsync(s => s.ProductLineId == colour.ProductLineId
            && _db.MaterialPrices.Any(p => p.ColourId == colour.Id && p.SheetSizeId == s.Id));

        var result = await _resolver.ResolveAsync(new PriceKey(colour.Id, size.Id), Today);

        Assert.True(result.Resolved);
        Assert.Equal((decimal)sheetPrice, result.UnitPrice);
    }

    [Fact]
    public async Task A_database_seeded_with_the_old_max_on_top_prices_is_corrected_and_a_price_since_changed_is_kept()
    {
        // An earlier seeder stored R1 670,1230 per m2 for Arctica, worked back from a
        // rounded sheet price. A price the Managing Director typed is a rand or more
        // away from the list figure, and must survive the correction.
        var arctica = await _db.MaterialPrices.FirstAsync(p => p.Colour!.Name == "Arctica 9015");
        var bone = await _db.MaterialPrices.FirstAsync(p => p.Colour!.Name == "Bone 8010");
        arctica.PricePerSqm = 1670.1230m;
        bone.PricePerSqm = 1715m;
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await CatalogueSeeder.SeedAsync(_db);
        _db.ChangeTracker.Clear();

        Assert.Equal(1670m, (await _db.MaterialPrices.AsNoTracking().FirstAsync(p => p.Id == arctica.Id)).PricePerSqm);
        Assert.Equal(1715m, (await _db.MaterialPrices.AsNoTracking().FirstAsync(p => p.Id == bone.Id)).PricePerSqm);
    }

    [Fact]
    public async Task The_seed_follows_the_supplier_sheets_on_phase_outs_sizes_and_thickness()
    {
        var phasing = await _db.Colours.AsNoTracking()
            .Where(c => c.Status == CatalogueStatus.PhasingOut)
            .Select(c => c.Name)
            .OrderBy(n => n)
            .ToListAsync();
        Assert.Equal(new[] { "Alaskan Stone 4312", "Bronze 7830", "Jurassic 7711", "Malt 7810", "Snowfall 8090" }, phasing);

        var infinito6 = await _db.SheetSizes.AsNoTracking()
            .SingleAsync(s => s.ProductLine!.Name == "Infinito Full Acrylic" && s.ProductLine.ThicknessMm == 6);
        Assert.Equal((3050, 750), (infinito6.LengthMm, infinito6.WidthMm));

        var wide = await _db.SheetSizes.AsNoTracking().Include(s => s.ProductLine)
            .SingleAsync(s => s.LengthMm == 3660 && s.WidthMm == 900);
        var classicWhite900 = await _db.Colours.AsNoTracking().SingleAsync(c => c.Name == "Classic White 900");
        Assert.Equal(20, wide.ProductLine!.ThicknessMm);
        Assert.Equal(wide.ProductLineId, classicWhite900.ProductLineId);
    }

    [Fact]
    public async Task A_database_seeded_before_the_supplier_sheet_corrections_is_put_right()
    {
        // Put the catalogue back the way the earlier seeder left it.
        foreach (var colour in await _db.Colours.Where(c => c.Status == CatalogueStatus.PhasingOut).ToListAsync())
            colour.Status = CatalogueStatus.Active;
        var infinito6 = await _db.SheetSizes.SingleAsync(s => s.ProductLine!.Name == "Infinito Full Acrylic" && s.ProductLine.ThicknessMm == 6);
        infinito6.WidthMm = 760;
        var perago12 = await _db.ProductLines.SingleAsync(p => p.Name == "Perago 100% Acrylic" && p.ThicknessMm == 12);
        var perago20 = await _db.ProductLines.SingleAsync(p => p.Name == "Perago 100% Acrylic" && p.ThicknessMm == 20);
        var wide = await _db.SheetSizes.SingleAsync(s => s.LengthMm == 3660 && s.WidthMm == 900);
        var classicWhite900 = await _db.Colours.SingleAsync(c => c.Name == "Classic White 900");
        wide.ProductLineId = perago12.Id;
        classicWhite900.ProductLineId = perago12.Id;
        await _db.SaveChangesAsync();
        _db.ProductLines.Remove(perago20);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await CatalogueSeeder.SeedAsync(_db);
        await CatalogueSeeder.SeedAsync(_db);   // a second start-up changes nothing more
        _db.ChangeTracker.Clear();

        Assert.Equal(5, await _db.Colours.CountAsync(c => c.Status == CatalogueStatus.PhasingOut));
        Assert.Equal(750, (await _db.SheetSizes.SingleAsync(s => s.Id == infinito6.Id)).WidthMm);
        var moved = await _db.SheetSizes.Include(s => s.ProductLine).SingleAsync(s => s.Id == wide.Id);
        Assert.Equal(20, moved.ProductLine!.ThicknessMm);
        Assert.Equal(moved.ProductLineId, (await _db.Colours.SingleAsync(c => c.Id == classicWhite900.Id)).ProductLineId);
        Assert.Equal(1, await _db.ProductLines.CountAsync(p => p.Name == "Perago 100% Acrylic" && p.ThicknessMm == 20));
    }

    [Fact]
    public async Task A_staron_band_price_matches_the_published_sheet_price()
    {
        var colour = await _db.Colours
            .AsNoTracking()
            .FirstAsync(c => c.Name == "Supreme");

        var size = await _db.SheetSizes
            .AsNoTracking()
            .FirstAsync(s => s.ProductLineId == colour.ProductLineId && s.LengthMm == 3680 && s.WidthMm == 760);

        var result = await _resolver.ResolveAsync(new PriceKey(colour.Id, size.Id), Today);

        Assert.True(result.Resolved);
        Assert.Equal(7719.17m, result.UnitPrice);   // published on the March 2025 list
    }

    [Fact]
    public async Task No_staron_colour_carries_a_made_up_supplier_code()
    {
        // The Staron list gives no product codes. A code that matches nothing on the
        // supplier's list would be worse than none on an order.
        var staron = await _db.Colours.AsNoTracking()
            .Where(c => c.ProductLine!.Supplier!.Name == "Staron (Salvocorp)")
            .ToListAsync();

        Assert.Equal(12, staron.Count);
        Assert.All(staron, c => Assert.Equal("", c.SupplierCode));
    }

    [Fact]
    public async Task A_database_seeded_with_made_up_staron_codes_has_them_cleared()
    {
        // A database seeded before the fix holds codes such as STARON-SUPREME.
        var supreme = await _db.Colours.FirstAsync(c => c.Name == "Supreme");
        supreme.SupplierCode = "STARON-SUPREME";
        var surfaceStudio = await _db.Colours.FirstAsync(c => c.SupplierCode == "SS-A-INF-003");
        await _db.SaveChangesAsync();

        await CatalogueSeeder.SeedAsync(_db);

        Assert.Equal("", (await _db.Colours.AsNoTracking().FirstAsync(c => c.Id == supreme.Id)).SupplierCode);
        Assert.Equal("SS-A-INF-003", (await _db.Colours.AsNoTracking().FirstAsync(c => c.Id == surfaceStudio.Id)).SupplierCode);
    }

    [Fact]
    public async Task A_code_entered_for_a_staron_colour_is_left_alone()
    {
        var supreme = await _db.Colours.FirstAsync(c => c.Name == "Supreme");
        supreme.SupplierCode = "SU-123";
        await _db.SaveChangesAsync();

        await CatalogueSeeder.SeedAsync(_db);

        Assert.Equal("SU-123", (await _db.Colours.AsNoTracking().FirstAsync(c => c.Id == supreme.Id)).SupplierCode);
    }

    [Fact]
    public async Task A_max_on_top_item_price_matches_the_published_sheet_price()
    {
        var colour = await _db.Colours
            .AsNoTracking()
            .FirstAsync(c => c.SupplierCode == "WSOLIDSURFACE80167");

        var size = await _db.SheetSizes
            .AsNoTracking()
            .FirstAsync(s => s.ProductLineId == colour.ProductLineId && s.LengthMm == 3680 && s.WidthMm == 760);

        var result = await _resolver.ResolveAsync(new PriceKey(colour.Id, size.Id), Today);

        Assert.True(result.Resolved);
        Assert.Equal(3991.03m, result.UnitPrice);   // R1 427 per m2 x 2,7968 m2 on the August 2026 list
    }

    [Fact]
    public async Task Overtime_is_expressed_as_a_multiple_of_fabrication_rather_than_a_separate_figure()
    {
        // The spreadsheet's core weakness was a rate card duplicated across twelve
        // sheets that drifted apart. The relationship is held once.
        var normal = await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == "Fabrication — no backsplash, normal");
        var overtime = await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == "Fabrication — no backsplash, overtime");

        Assert.Equal(normal.Id, overtime.DerivedFromRateItemId);
        Assert.Equal(1.5m, overtime.DerivedFromRateItemMultiplier);
        Assert.False(await _db.RatePrices.AnyAsync(p => p.RateItemId == overtime.Id));
    }

    [Fact]
    public async Task The_rates_awaiting_the_client_are_seeded_without_a_price()
    {
        // These lines have no price on any sheet of the client's workbook. They are
        // left unpriced so the system reports them as unresolved rather than
        // inventing a plausible number, which is the failure the project exists to
        // remove.
        foreach (var name in RateCardSeeder.AwaitingClientRates)
        {
            var item = await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == name);
            var hasPrice = await _db.RatePrices.AsNoTracking().AnyAsync(p => p.RateItemId == item.Id);

            Assert.False(hasPrice, $"{name} should have no seeded price until the client supplies one.");
        }
    }

    [Fact]
    public async Task Supplier_delivery_charges_are_not_seeded_as_a_rate()
    {
        // R1 050, R550 and R510 are what a supplier charges Techno Surfaces for
        // delivery. Transport on a quote is the amount the Managing Director types
        // per job, so no rate on the card varies by supplier.
        Assert.False(await _db.RatePrices.AnyAsync(p => p.SupplierId != null));
        Assert.False(await _db.RateItems.AnyAsync(r => r.Name == "Transport"));
    }
}
