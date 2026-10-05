using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Auditing;

/// <summary>
/// NFR-04: every price change is recorded with the user, the time, and the old
/// and new value. One of the security tests in the build plan.
/// </summary>
public sealed class AuditInterceptorTests : IAsyncLifetime
{
    private const string ManagingDirectorId = "md-user-id";

    private sealed class FixedUser : ICurrentUser
    {
        public string UserId => ManagingDirectorId;
    }

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;

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
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<RatePrice> AddRatePriceAsync(decimal amount)
    {
        var item = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        _db.RateItems.Add(item);
        await _db.SaveChangesAsync();

        var price = new RatePrice { RateItemId = item.Id, Amount = amount, EffectiveFrom = new DateOnly(2026, 1, 1) };
        _db.RatePrices.Add(price);
        await _db.SaveChangesAsync();

        return price;
    }

    [Fact]
    public async Task Changing_a_price_records_the_old_value_new_value_user_and_time()
    {
        var price = await AddRatePriceAsync(450.00m);
        var before = DateTime.UtcNow;

        price.Amount = 495.00m;
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) &&
            a.PropertyName == nameof(RatePrice.Amount) &&
            a.OldValue != null);

        Assert.Equal(price.Id.ToString(), entry.EntityKey);
        Assert.Equal("450.00", entry.OldValue);
        Assert.Equal("495.00", entry.NewValue);
        Assert.Equal(ManagingDirectorId, entry.UserId);
        Assert.InRange(entry.ChangedAtUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task A_new_price_is_recorded_against_its_real_key()
    {
        var price = await AddRatePriceAsync(450.00m);

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) && a.PropertyName == nameof(RatePrice.Amount));

        Assert.Equal(price.Id.ToString(), entry.EntityKey);
        Assert.Null(entry.OldValue);
        Assert.Equal("450.00", entry.NewValue);
    }

    [Fact]
    public async Task Entities_outside_the_audited_set_are_not_recorded()
    {
        await AddRatePriceAsync(450.00m);

        Assert.False(await _db.AuditEntries.AnyAsync(a => a.EntityName == nameof(RateItem)));
    }

    [Fact]
    public async Task Saving_without_changes_writes_no_audit_rows()
    {
        await AddRatePriceAsync(450.00m);
        var count = await _db.AuditEntries.CountAsync();

        await _db.SaveChangesAsync();

        Assert.Equal(count, await _db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task A_row_saved_with_its_parent_records_the_parents_real_key()
    {
        var item = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        var price = new RatePrice { RateItem = item, Amount = 450.00m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        _db.RatePrices.Add(price);   // the item is new too, so its id is temporary until the save
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) && a.PropertyName == nameof(RatePrice.RateItemId));

        Assert.Equal(item.Id.ToString(), entry.NewValue);
    }
}