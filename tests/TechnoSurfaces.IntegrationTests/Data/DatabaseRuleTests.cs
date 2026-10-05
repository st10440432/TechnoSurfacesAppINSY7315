using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.IntegrationTests.Infrastructure;

namespace TechnoSurfaces.IntegrationTests.Data;

/// <summary>
/// The rules the database enforces on its own, proved on SQL Server: the overlap
/// triggers, the filtered unique indexes and the check constraints. SQLite, which the
/// unit tests use, cannot run triggers or filtered indexes the same way, so these
/// rules were untested until the harness existed.
///
/// Each test writes straight to the database with SQL, bypassing the domain and the
/// services, so the refusal can only have come from the database. Every write is made
/// inside a transaction that is rolled back, so nothing is left behind for the other
/// tests that share this database.
/// </summary>
[Collection(IntegrationCollection.Name)]
public sealed class DatabaseRuleTests
{
    // SQL Server error numbers.
    private const int UniqueIndexViolation = 2601;
    private const int CheckConstraintViolation = 547;
    private const int MaterialPriceOverlap = 51001;
    private const int RatePriceOverlap = 51002;

    private readonly AppFactory _app;

    public DatabaseRuleTests(AppFactory app) => _app = app;

    /// <summary>
    /// Runs <paramref name="work"/> in a transaction that is always rolled back, and
    /// returns the SqlException it threw.
    /// </summary>
    private async Task<SqlException> RefusedAsync(Func<TechnoSurfacesDbContext, Task> work)
    {
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TechnoSurfacesDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        return await Assert.ThrowsAsync<SqlException>(() => work(db));
    }

    private static DateTime Day(DateOnly date) => date.ToDateTime(TimeOnly.MinValue);

    // ---- Material prices ----

    [Fact]
    public async Task A_second_open_price_for_the_same_band_and_sheet_size_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.MaterialPrices.AsNoTracking()
                .FirstAsync(p => p.PriceBandId != null && p.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO MaterialPrices (PriceBandId, SheetSizeId, PricePerSqm, EffectiveFrom, EffectiveTo, CapturedAtUtc)
                VALUES ({open.PriceBandId}, {open.SheetSizeId}, {open.PricePerSqm + 1m}, {Day(open.EffectiveFrom.AddDays(1))}, NULL, SYSUTCDATETIME())");
        });

        Assert.Equal(UniqueIndexViolation, error.Number);
        Assert.Contains("UX_MaterialPrices_OneOpenPricePerBand", error.Message);
    }

    [Fact]
    public async Task A_second_open_price_for_the_same_colour_and_sheet_size_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.MaterialPrices.AsNoTracking()
                .FirstAsync(p => p.ColourId != null && p.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO MaterialPrices (ColourId, SheetSizeId, PricePerSqm, EffectiveFrom, EffectiveTo, CapturedAtUtc)
                VALUES ({open.ColourId}, {open.SheetSizeId}, {open.PricePerSqm + 1m}, {Day(open.EffectiveFrom.AddDays(1))}, NULL, SYSUTCDATETIME())");
        });

        Assert.Equal(UniqueIndexViolation, error.Number);
        Assert.Contains("UX_MaterialPrices_OneOpenPricePerColour", error.Message);
    }

    [Fact]
    public async Task A_closed_price_period_that_overlaps_the_current_price_is_refused_and_one_before_it_is_accepted()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.MaterialPrices.AsNoTracking()
                .FirstAsync(p => p.PriceBandId != null && p.EffectiveTo == null);

            // A period that ends the day before the current price starts does not
            // overlap it, so the trigger lets it through. Asserted here, because a
            // refusal of this row would throw the same error the test expects below.
            var accepted = await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO MaterialPrices (PriceBandId, SheetSizeId, PricePerSqm, EffectiveFrom, EffectiveTo, CapturedAtUtc)
                VALUES ({open.PriceBandId}, {open.SheetSizeId}, {open.PricePerSqm}, {Day(open.EffectiveFrom.AddDays(-60))}, {Day(open.EffectiveFrom.AddDays(-1))}, SYSUTCDATETIME())");
            Assert.Equal(1, accepted);

            // A closed period inside the current price's period is the case the
            // filtered index cannot see, because neither row would be open-ended
            // and new. Only the trigger catches it.
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO MaterialPrices (PriceBandId, SheetSizeId, PricePerSqm, EffectiveFrom, EffectiveTo, CapturedAtUtc)
                VALUES ({open.PriceBandId}, {open.SheetSizeId}, {open.PricePerSqm}, {Day(open.EffectiveFrom.AddDays(1))}, {Day(open.EffectiveFrom.AddDays(10))}, SYSUTCDATETIME())");
        });

        Assert.Equal(MaterialPriceOverlap, error.Number);
    }

    [Fact]
    public async Task A_material_price_of_zero_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.MaterialPrices.AsNoTracking()
                .FirstAsync(p => p.PriceBandId != null && p.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE MaterialPrices SET PricePerSqm = 0 WHERE Id = {open.Id}");
        });

        Assert.Equal(CheckConstraintViolation, error.Number);
        Assert.Contains("CK_MaterialPrice_Positive", error.Message);
    }

    [Fact]
    public async Task A_material_price_that_ends_before_it_starts_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.MaterialPrices.AsNoTracking()
                .FirstAsync(p => p.PriceBandId != null && p.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE MaterialPrices SET EffectiveTo = {Day(open.EffectiveFrom.AddDays(-1))} WHERE Id = {open.Id}");
        });

        Assert.Equal(CheckConstraintViolation, error.Number);
        Assert.Contains("CK_MaterialPrice_Period", error.Message);
    }

    // ---- Rates ----

    [Fact]
    public async Task A_second_open_general_rate_for_the_same_item_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.RatePrices.AsNoTracking()
                .FirstAsync(r => r.SupplierId == null && r.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO RatePrices (RateItemId, SupplierId, Amount, EffectiveFrom, EffectiveTo)
                VALUES ({open.RateItemId}, NULL, {open.Amount + 1m}, {Day(open.EffectiveFrom.AddDays(1))}, NULL)");
        });

        Assert.Equal(UniqueIndexViolation, error.Number);
        Assert.Contains("UX_RatePrices_OneOpenRate", error.Message);
    }

    [Fact]
    public async Task A_closed_rate_period_that_overlaps_the_current_rate_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.RatePrices.AsNoTracking()
                .FirstAsync(r => r.SupplierId == null && r.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO RatePrices (RateItemId, SupplierId, Amount, EffectiveFrom, EffectiveTo)
                VALUES ({open.RateItemId}, NULL, {open.Amount}, {Day(open.EffectiveFrom.AddDays(1))}, {Day(open.EffectiveFrom.AddDays(10))})");
        });

        Assert.Equal(RatePriceOverlap, error.Number);
    }

    [Fact]
    public async Task A_rate_of_zero_is_refused()
    {
        var error = await RefusedAsync(async db =>
        {
            var open = await db.RatePrices.AsNoTracking().FirstAsync(r => r.EffectiveTo == null);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE RatePrices SET Amount = 0 WHERE Id = {open.Id}");
        });

        Assert.Equal(CheckConstraintViolation, error.Number);
        Assert.Contains("CK_RatePrice_Positive", error.Message);
    }

    // ---- Costing lines (NFR-01) ----

    /// <summary>A draft quote with one rate line, saved inside the caller's transaction.</summary>
    private static async Task<int> CostingLineAsync(TechnoSurfacesDbContext db)
    {
        var customer = await db.Customers.Include(c => c.Contacts).FirstAsync(c => c.Contacts.Any());
        var rate = await db.RatePrices.AsNoTracking().FirstAsync(r => r.EffectiveTo == null);

        var quote = new Quote("IT-DB-" + Guid.NewGuid().ToString("N")[..8], customer.Id, customer.Contacts.First().Id,
            "integration-test", DateOnly.FromDateTime(DateTime.UtcNow));
        var version = quote.StartNewVersion("integration-test", 40m);
        var line = CostingLine.ForRate(rate.RateItemId, "Rate line", rate.Amount, "Rate card", 1m, isBelowTheLine: false);
        version.AddCostingLine(line);

        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return line.Id;
    }

    [Fact]
    public async Task A_costing_line_priced_at_zero_is_refused_by_the_database_itself()
    {
        var error = await RefusedAsync(async db =>
        {
            var lineId = await CostingLineAsync(db);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE CostingLines SET ResolvedUnitPrice = 0 WHERE Id = {lineId}");
        });

        Assert.Equal(CheckConstraintViolation, error.Number);
        Assert.Contains("CK_CostingLine_PricePositive", error.Message);
    }

    [Fact]
    public async Task A_rate_typed_on_the_quote_of_zero_is_refused_by_the_database_itself()
    {
        var error = await RefusedAsync(async db =>
        {
            var lineId = await CostingLineAsync(db);

            await db.Database.ExecuteSqlInterpolatedAsync($@"
                UPDATE CostingLines SET OverriddenUnitPrice = 0 WHERE Id = {lineId}");
        });

        Assert.Equal(CheckConstraintViolation, error.Number);
        Assert.Contains("CK_CostingLine_OverridePositive", error.Message);
    }
}
