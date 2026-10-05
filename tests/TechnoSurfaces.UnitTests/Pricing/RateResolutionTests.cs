using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Pricing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Pricing;

/// <summary>
/// Covers the rate card: the figures seeded from the client's costing workbook,
/// rates that are derived from another rate rather than maintained separately, and
/// supplier-specific amounts.
/// </summary>
public sealed class RateResolutionTests : IAsyncLifetime
{
    private const string NoBacksplash = "Fabrication — no backsplash, normal";
    private const string WithBacksplash = "Fabrication — with backsplash, normal";
    private const string NoBacksplashOvertime = "Fabrication — no backsplash, overtime";
    private const string WithBacksplashOvertime = "Fabrication — with backsplash, overtime";
    private const string Installation = "Installation — normal";
    private const string InstallationOvertime = "Installation — overtime";

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private IRateResolver _rates = null!;

    private static readonly DateOnly Today = new(2026, 9, 29);

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        await CatalogueSeeder.SeedAsync(_db);
        await RateCardSeeder.SeedAsync(_db);

        _rates = new RateResolver(new CatalogueReader(_db));
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<int> RateIdAsync(string name) =>
        (await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == name)).Id;

    private async Task<int> SupplierIdAsync(string name) =>
        (await _db.Suppliers.AsNoTracking().FirstAsync(s => s.Name == name)).Id;

    private async Task<decimal> ResolveAsync(string name, DateOnly? asAt = null)
    {
        var result = await _rates.ResolveAsync(await RateIdAsync(name), null, asAt ?? Today);
        Assert.True(result.Resolved, $"{name} did not resolve: {result.FailureReason}");
        return result.UnitPrice;
    }

    [Theory]
    [InlineData(NoBacksplash, "265.00")]
    [InlineData(WithBacksplash, "285.00")]
    [InlineData("Thermoforming", "400.00")]
    [InlineData("Vacuum press", "400.00")]
    [InlineData("Sanding time", "100.00")]
    [InlineData("Seamkit", "220.00")]
    [InlineData("Sandpaper & consumables", "55.00")]
    [InlineData("Silicon + sealing", "55.00")]
    [InlineData("Genkem", "140.00")]
    [InlineData("MDF Bison 16mm white face", "1027.20")]
    [InlineData("MDF Bison 16mm", "738.30")]
    [InlineData("MDF Bison 12mm", "726.53")]
    [InlineData("MDF Bison 9mm", "583.15")]
    [InlineData("Chipboard 16mm", "512.53")]
    [InlineData("Hardboard std 3.2", "134.00")]
    [InlineData("Plywood Pine 18mm", "559.00")]
    [InlineData("Drainer grooves", "175.00")]
    [InlineData("Sink / vanity cut out", "225.00")]
    [InlineData("Underslung sink / vanity", "375.00")]
    [InlineData("Hob cut out", "350.00")]
    public async Task Each_seeded_rate_matches_the_highest_figure_in_the_client_workbook(string name, string expected)
    {
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), await ResolveAsync(name));
    }

    [Fact]
    public async Task Overtime_resolves_as_one_and_a_half_times_the_normal_rate()
    {
        Assert.Equal(397.50m, await ResolveAsync(NoBacksplashOvertime));     // 265,00 x 1,5
        Assert.Equal(427.50m, await ResolveAsync(WithBacksplashOvertime));   // 285,00 x 1,5

        var result = await _rates.ResolveAsync(await RateIdAsync(NoBacksplashOvertime), null, Today);
        Assert.Contains("x 1.5", result.Origin);
    }

    [Fact]
    public async Task Installation_mirrors_the_no_backsplash_fabrication_rates()
    {
        // Workbook: installation normal = F8 (no backsplash, normal) and
        // installation overtime = F10 (no backsplash, overtime).
        Assert.Equal(265.00m, await ResolveAsync(Installation));
        Assert.Equal(397.50m, await ResolveAsync(InstallationOvertime));

        var result = await _rates.ResolveAsync(await RateIdAsync(Installation), null, Today);
        Assert.Contains("mirrors", result.Origin);
    }

    [Fact]
    public async Task Changing_the_fabrication_rate_carries_through_to_overtime_and_installation()
    {
        var normalId = await RateIdAsync(NoBacksplash);
        var seeded = await _db.RatePrices.SingleAsync(p => p.RateItemId == normalId);

        seeded.EffectiveTo = new DateOnly(2026, 6, 30);
        _db.RatePrices.Add(new RatePrice { RateItemId = normalId, Amount = 300.00m, EffectiveFrom = new DateOnly(2026, 7, 1) });
        await _db.SaveChangesAsync();

        Assert.Equal(397.50m, await ResolveAsync(NoBacksplashOvertime, new DateOnly(2026, 3, 1)));
        Assert.Equal(450.00m, await ResolveAsync(NoBacksplashOvertime));   // 300,00 x 1,5
        Assert.Equal(300.00m, await ResolveAsync(Installation));
        Assert.Equal(450.00m, await ResolveAsync(InstallationOvertime));
    }

    [Fact]
    public async Task A_supplier_specific_rate_is_used_in_place_of_the_general_one()
    {
        // The model allows a rate to vary by supplier. None is seeded, because the
        // team decided on one seamkit line, but the Managing Director can add one.
        var seamkit = await RateIdAsync("Seamkit");
        var woodcentre = await SupplierIdAsync("Woodcentre CPT");

        _db.RatePrices.Add(new RatePrice
        {
            RateItemId = seamkit,
            SupplierId = woodcentre,
            Amount = 250.00m,
            EffectiveFrom = new DateOnly(2026, 1, 1)
        });
        await _db.SaveChangesAsync();

        var forWoodcentre = await _rates.ResolveAsync(seamkit, woodcentre, Today);
        var forOthers = await _rates.ResolveAsync(seamkit, await SupplierIdAsync("Max on Top"), Today);

        Assert.Equal(250.00m, forWoodcentre.UnitPrice);
        Assert.Equal(220.00m, forOthers.UnitPrice);
    }

    [Fact]
    public async Task A_rate_the_client_has_not_supplied_does_not_resolve_and_says_so()
    {
        foreach (var name in RateCardSeeder.AwaitingClientRates)
        {
            var result = await _rates.ResolveAsync(await RateIdAsync(name), null, Today);

            Assert.False(result.Resolved);
            Assert.Contains(name, result.FailureReason!);
            Assert.Throws<PriceNotResolvedException>(() => result.UnitPrice);
        }
    }

    [Fact]
    public async Task A_derived_rate_whose_base_is_unpriced_explains_which_rate_is_missing()
    {
        var sink = await RateIdAsync("Sink / vanity");
        var derived = new RateItem
        {
            Name = "Derived from sink",
            Category = RateCategory.SinksAndHardware,
            Unit = ChargeUnit.Each,
            DerivedFromRateItemId = sink,
            DerivedFromRateItemMultiplier = 1m
        };
        _db.RateItems.Add(derived);
        await _db.SaveChangesAsync();

        var result = await _rates.ResolveAsync(derived.Id, null, Today);

        Assert.False(result.Resolved);
        Assert.Contains("Sink / vanity", result.FailureReason!);
    }

    [Fact]
    public async Task A_rate_card_configured_to_derive_from_itself_is_refused_rather_than_looping()
    {
        var a = new RateItem { Name = "Cycle A", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        var b = new RateItem { Name = "Cycle B", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        _db.RateItems.AddRange(a, b);
        await _db.SaveChangesAsync();

        a.DerivedFromRateItemId = b.Id;
        a.DerivedFromRateItemMultiplier = 1m;
        b.DerivedFromRateItemId = a.Id;
        b.DerivedFromRateItemMultiplier = 1m;
        await _db.SaveChangesAsync();

        var result = await _rates.ResolveAsync(a.Id, null, Today);

        Assert.False(result.Resolved);
        Assert.Contains("derives from itself", result.FailureReason!);
    }

    [Fact]
    public async Task Silicon_is_two_per_sheet_and_sandpaper_follows_the_total_area()
    {
        var silicon = await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == "Silicon + sealing");
        var sandpaper = await _db.RateItems.AsNoTracking().FirstAsync(r => r.Name == "Sandpaper & consumables");

        Assert.Equal(DerivationRule.FromSheetCount, silicon.Derivation);
        Assert.Equal(2m, silicon.DerivationFactor);
        Assert.Equal(DerivationRule.FromTotalAreaM2, sandpaper.Derivation);
        Assert.Equal(1m, sandpaper.DerivationFactor);
    }

    [Fact]
    public async Task Only_the_cut_out_and_groove_charges_sit_below_the_markup_line()
    {
        // Workbook: SUB TOTAL sums every line above row 44, so consumables, wood and
        // sinks are marked up. Drainer grooves and the three cut-out charges sit
        // between Mark up and TOTAL.
        var below = await _db.RateItems.AsNoTracking()
            .Where(r => r.IsBelowTheLine)
            .OrderBy(r => r.SortOrder)
            .Select(r => r.Name)
            .ToListAsync();

        Assert.Equal(new[] { "Drainer grooves", "Sink / vanity cut out", "Underslung sink / vanity", "Hob cut out" }, below);
    }

    [Fact]
    public async Task The_rate_card_follows_the_order_of_the_client_workbook()
    {
        var items = await _db.RateItems.AsNoTracking().OrderBy(r => r.SortOrder).ToListAsync();

        Assert.Equal(33, items.Count);
        Assert.Equal(NoBacksplash, items.First().Name);
        Assert.Equal("Hob cut out", items.Last().Name);
        Assert.Equal(items.Count, items.Select(r => r.SortOrder).Distinct().Count());
        Assert.Equal(13, items.Count(r => r.Category == RateCategory.Wood));
    }
}
