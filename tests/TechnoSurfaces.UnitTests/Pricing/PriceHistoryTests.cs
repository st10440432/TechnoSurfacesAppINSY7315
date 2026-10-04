using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Pricing;

/// <summary>
/// For one price key, exactly one price is in force on any day. A change closes the
/// current price and adds a new one; it never overwrites, so a quote dated before
/// the change keeps resolving to the figure it was priced at.
/// </summary>
public sealed class PriceHistoryTests : IAsyncLifetime
{
    private const string NoBacksplash = "Fabrication — no backsplash, normal";

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private IPriceHistory _history = null!;
    private IRateResolver _rates = null!;
    private IPriceResolver _prices = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(_db);
        await RateCardSeeder.SeedAsync(_db);

        var catalogue = new CatalogueReader(_db);
        _history = new PriceHistory(_db);
        _rates = new RateResolver(catalogue);
        _prices = new PriceResolver(catalogue, new IPriceResolutionStrategy[]
        {
            new BandPricedStrategy(catalogue), new ItemPricedStrategy(catalogue)
        });
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<int> RateIdAsync(string name) =>
        (await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == name)).Id;

    // ------------------------------------------------------------ the domain rule

    [Fact]
    public void Superseding_closes_the_current_price_on_the_day_before()
    {
        var current = new RatePrice { RateItemId = 1, Amount = 265m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        var next = current.Supersede(290m, new DateOnly(2026, 11, 1));

        Assert.Equal(new DateOnly(2026, 10, 31), current.EffectiveTo);
        Assert.Equal(new DateOnly(2026, 11, 1), next.EffectiveFrom);
        Assert.Null(next.EffectiveTo);
        Assert.Equal(1, next.RateItemId);
    }

    [Fact]
    public void A_material_price_keeps_its_key_when_superseded()
    {
        var current = new MaterialPrice { PriceBandId = 4, SheetSizeId = 7, PricePerSqm = 1550m, EffectiveFrom = new DateOnly(2025, 3, 1) };

        var next = current.Supersede(1600m, new DateOnly(2026, 11, 1), "md");

        Assert.Equal(4, next.PriceBandId);
        Assert.Null(next.ColourId);
        Assert.Equal(7, next.SheetSizeId);
        Assert.Equal("md", next.CapturedByUserId);
    }

    [Fact]
    public void A_price_that_has_already_ended_cannot_be_superseded()
    {
        var ended = new RatePrice { Amount = 265m, EffectiveFrom = new DateOnly(2026, 1, 1), EffectiveTo = new DateOnly(2026, 6, 30) };

        Assert.Throws<InvalidOperationException>(() => ended.Supersede(290m, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public void A_replacement_must_start_after_the_current_price()
    {
        var current = new RatePrice { Amount = 265m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => current.Supersede(290m, new DateOnly(2026, 1, 1)));
    }

    [Fact]
    public void A_material_price_of_zero_is_refused()
    {
        var current = new MaterialPrice { ColourId = 1, SheetSizeId = 1, PricePerSqm = 1000m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => current.Supersede(0m, new DateOnly(2026, 2, 1), "md"));
    }

    [Fact]
    public void A_rate_of_zero_is_refused()
    {
        var current = new RatePrice { RateItemId = 1, Amount = 265m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        Assert.Throws<ArgumentOutOfRangeException>(() => current.Supersede(0m, new DateOnly(2026, 2, 1)));
    }

    [Fact]
    public async Task A_first_rate_of_zero_is_refused_and_the_item_stays_unresolved()
    {
        // A rate the client has not supplied stays unresolved. Setting it to zero
        // would turn a visible gap back into a plausible figure.
        var sink = await RateIdAsync("Sink / vanity");

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _history.SetRateAsync(sink, null, 0m, new DateOnly(2026, 10, 2)));

        Assert.False((await _rates.ResolveAsync(sink, null, new DateOnly(2026, 10, 2))).Resolved);
    }

    // ------------------------------------------------------------ the service

    [Fact]
    public async Task A_rate_change_applies_from_its_date_and_earlier_quotes_keep_the_old_rate()
    {
        var fabrication = await RateIdAsync(NoBacksplash);

        await _history.SetRateAsync(fabrication, null, 290m, new DateOnly(2026, 11, 1));

        var before = await _rates.ResolveAsync(fabrication, null, new DateOnly(2026, 10, 31));
        var after = await _rates.ResolveAsync(fabrication, null, new DateOnly(2026, 11, 1));

        Assert.Equal(265.00m, before.UnitPrice);
        Assert.Equal(290.00m, after.UnitPrice);
        Assert.Equal(2, await _db.RatePrices.CountAsync(p => p.RateItemId == fabrication));
    }

    [Fact]
    public async Task A_rate_awaiting_the_client_resolves_once_the_managing_director_sets_it()
    {
        var sink = await RateIdAsync("Sink / vanity");
        Assert.False((await _rates.ResolveAsync(sink, null, new DateOnly(2026, 10, 2))).Resolved);

        await _history.SetRateAsync(sink, null, 1300m, new DateOnly(2026, 10, 2));

        Assert.Equal(1300.00m, (await _rates.ResolveAsync(sink, null, new DateOnly(2026, 10, 2))).UnitPrice);
    }

    [Fact]
    public async Task A_calculated_rate_cannot_be_given_a_figure_of_its_own()
    {
        // Overtime is the normal rate x 1.5. Pricing it separately would bring back
        // the duplicated figures the rate card exists to remove.
        var overtime = await RateIdAsync("Fabrication — no backsplash, overtime");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _history.SetRateAsync(overtime, null, 500m, new DateOnly(2026, 11, 1)));
    }

    [Fact]
    public async Task A_band_price_change_reaches_every_colour_in_the_band_from_its_date()
    {
        var colour = await _db.Colours.AsNoTracking().FirstAsync(c => c.Name == "Supreme");
        var size = await _db.SheetSizes.AsNoTracking().FirstAsync(s => s.ProductLineId == colour.ProductLineId);

        await _history.SetMaterialPriceAsync(null, colour.PriceBandId, size.Id, 2900m, new DateOnly(2026, 11, 1), "md");

        var before = await _prices.ResolveAsync(new PriceKey(colour.Id, size.Id), new DateOnly(2026, 10, 31));
        var after = await _prices.ResolveAsync(new PriceKey(colour.Id, size.Id), new DateOnly(2026, 11, 1));

        Assert.Equal(7719.17m, before.UnitPrice);    // R2 760,00/m2 x 2,7968
        Assert.Equal(8110.72m, after.UnitPrice);     // R2 900,00/m2 x 2,7968
    }

    [Fact]
    public async Task A_material_price_must_name_a_colour_or_a_band()
    {
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _history.SetMaterialPriceAsync(1, 1, 1, 1000m, new DateOnly(2026, 11, 1), "md"));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            _history.SetMaterialPriceAsync(null, null, 1, 1000m, new DateOnly(2026, 11, 1), "md"));
    }

    // ------------------------------------------------------------ the database

    // These run on SQLite. Two of the rules behave differently there and are
    // verified against SQL Server instead: SQLite treats nulls as distinct in a
    // unique index, so the one-open-general-rate case cannot be shown here, and it
    // stores decimals as text, so the price-above-zero check cannot be shown either.

    [Fact]
    public async Task The_database_refuses_a_second_open_price_for_the_same_rate_and_supplier()
    {
        var seamkit = await RateIdAsync("Seamkit");
        var woodcentre = (await _db.Suppliers.AsNoTracking().FirstAsync(s => s.Name == "Woodcentre CPT")).Id;
        _db.RatePrices.Add(new RatePrice { RateItemId = seamkit, SupplierId = woodcentre, Amount = 250m, EffectiveFrom = new DateOnly(2026, 1, 1) });
        await _db.SaveChangesAsync();

        _db.RatePrices.Add(new RatePrice { RateItemId = seamkit, SupplierId = woodcentre, Amount = 260m, EffectiveFrom = new DateOnly(2026, 11, 1) });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_period_that_ends_before_it_starts()
    {
        var price = await _db.RatePrices.FirstAsync();
        price.EffectiveTo = price.EffectiveFrom.AddDays(-1);

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }
}
