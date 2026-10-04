using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Catalogue;

/// <summary>US-23 and US-24 through the service the MD's screens use.</summary>
public sealed class CatalogueServiceTests : IAsyncLifetime
{
    private const string ManagingDirectorId = "md-user-id";

    private sealed class FixedUser : ICurrentUser
    {
        public string UserId => ManagingDirectorId;
    }

    private static readonly DateOnly OriginalFrom = new(2026, 1, 1);
    private static readonly DateOnly NewFrom = new(2026, 10, 1);

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private CatalogueService _service = null!;
    private int _colourId;
    private int _sizeId;

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

        var supplier = new Supplier { Name = "Woodcentre", PricingStructure = PricingStructure.Item, PriceListDated = OriginalFrom };
        _db.Suppliers.Add(supplier);
        await _db.SaveChangesAsync();

        var line = new ProductLine { SupplierId = supplier.Id, Name = "Corian", ThicknessMm = 12 };
        _db.ProductLines.Add(line);
        await _db.SaveChangesAsync();

        var size = new SheetSize { ProductLineId = line.Id, LengthMm = 3658, WidthMm = 760 };
        var colour = new Colour { ProductLineId = line.Id, Name = "Glacier White", SupplierCode = "GW" };
        _db.SheetSizes.Add(size);
        _db.Colours.Add(colour);
        await _db.SaveChangesAsync();

        _db.MaterialPrices.Add(new MaterialPrice
        {
            ColourId = colour.Id,
            SheetSizeId = size.Id,
            PricePerSqm = 100.00m,
            EffectiveFrom = OriginalFrom,
            CapturedAtUtc = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();

        _colourId = colour.Id;
        _sizeId = size.Id;
        _service = new CatalogueService(_db, new PriceHistory(_db), new FixedUser());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task A_price_change_supersedes_the_current_price_and_records_who_made_it()
    {
        var result = await _service.SetMaterialPriceAsync(_colourId, null, _sizeId, 120.00m, NewFrom);

        Assert.True(result.Succeeded, result.Error);
        var history = await _service.GetPriceHistoryAsync(_colourId, null, _sizeId);
        Assert.Equal(2, history.Count);
        Assert.Equal(120.00m, history[0].PricePerSqm);
        Assert.Equal(ManagingDirectorId, history[0].CapturedByUserId);
        Assert.Equal(NewFrom.AddDays(-1), history[1].To);
    }

    [Fact]
    public async Task A_zero_price_is_refused_with_a_message_not_an_exception()
    {
        var result = await _service.SetMaterialPriceAsync(_colourId, null, _sizeId, 0m, NewFrom);

        Assert.False(result.Succeeded);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
        Assert.Single(await _service.GetPriceHistoryAsync(_colourId, null, _sizeId));
    }

    [Fact]
    public async Task A_retired_colour_keeps_its_prices_and_cannot_be_repriced()
    {
        var retired = await _service.RetireColourAsync(_colourId);
        var repriced = await _service.SetMaterialPriceAsync(_colourId, null, _sizeId, 120.00m, NewFrom);

        Assert.True(retired.Succeeded);
        Assert.False(repriced.Succeeded);
        Assert.Equal(CatalogueStatus.Discontinued, (await _db.Colours.SingleAsync()).Status);
        Assert.Single(await _service.GetPriceHistoryAsync(_colourId, null, _sizeId));
    }

    [Fact]
    public async Task Retiring_a_colour_is_recorded_in_the_audit_trail()
    {
        await _service.RetireColourAsync(_colourId);

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(Colour) && a.PropertyName == nameof(Colour.Status) && a.OldValue != null);

        Assert.Equal(nameof(CatalogueStatus.Active), entry.OldValue);
        Assert.Equal(nameof(CatalogueStatus.Discontinued), entry.NewValue);
        Assert.Equal(ManagingDirectorId, entry.UserId);
    }

    [Fact]
    public async Task The_catalogue_shows_the_price_in_force_and_retired_colours()
    {
        await _service.SetMaterialPriceAsync(_colourId, null, _sizeId, 120.00m, NewFrom);
        await _service.RetireColourAsync(_colourId);

        var before = Assert.Single(await _service.GetCatalogueAsync(NewFrom.AddDays(-1)));
        var after = Assert.Single(await _service.GetCatalogueAsync(NewFrom));

        Assert.Equal(100.00m, before.PricePerSqm);
        Assert.Equal(120.00m, after.PricePerSqm);
        Assert.True(after.IsRetired);
    }

    [Fact]
    public async Task A_derived_rate_is_refused_and_shown_as_a_rule()
    {
        var normal = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        _db.RateItems.Add(normal);
        await _db.SaveChangesAsync();

        var overtime = new RateItem
        {
            Name = "Fabrication overtime",
            Category = RateCategory.Fabrication,
            Unit = ChargeUnit.Hour,
            DerivedFromRateItemId = normal.Id,
            DerivedFromRateItemMultiplier = 1.5m
        };
        _db.RateItems.Add(overtime);
        await _db.SaveChangesAsync();

        var result = await _service.SetRateAsync(overtime.Id, null, 600.00m, NewFrom);
        var row = (await _service.GetRateCardAsync(NewFrom)).Single(r => r.RateItemId == overtime.Id);

        Assert.False(result.Succeeded);
        Assert.True(row.IsDerived);
        Assert.Equal("Fabrication", row.DerivedFrom);
    }
}