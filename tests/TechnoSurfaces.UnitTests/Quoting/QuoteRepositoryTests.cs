using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The repository loads only a quote's current version for editing, approving and
/// reopening, and every version for the history screens (NFR-05). A quote reopened
/// several times must still be numbered, changed and saved correctly.
/// </summary>
public sealed class QuoteRepositoryTests : IAsyncLifetime
{
    private const string Author = "user-md";

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
        await RateCardSeeder.SeedAsync(db);
        await CustomerSeeder.SeedAsync(db);

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var sanding = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();

        // Three versions, each with one line: 1 hour of sanding on version 1, 2 on version 2, 3 on version 3.
        var quote = new Quote("TS-REPO-1", customer.Id, customer.Contacts.Single().Id, Author, new DateOnly(2026, 10, 1));
        for (var hours = 1; hours <= 3; hours++)
            quote.StartNewVersion(Author, markupPercent: 40m)
                .AddCostingLine(CostingLine.ForRate(sanding, "Sanding time", 100m, "Rate card: Sanding time", hours, isBelowTheLine: false));
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        _quoteId = quote.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task A_quote_loads_with_only_its_current_version()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        var quote = await new QuoteRepository(db).GetAsync(_quoteId);

        var version = Assert.Single(quote!.Versions);
        Assert.Equal(3, version.VersionNo);
        Assert.Equal(3m, Assert.Single(version.CostingLines).Quantity);
    }

    [Fact]
    public async Task Asking_for_every_version_completes_the_quote_and_keeps_a_change_already_made()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var repository = new QuoteRepository(db);

        var quote = await repository.GetAsync(_quoteId);
        var current = quote!.CurrentVersion!;
        current.ChangeQuantity(current.CostingLines.Single(), 7m);

        var complete = await repository.GetWithAllVersionsAsync(_quoteId);

        Assert.Same(quote, complete);
        Assert.Equal(new[] { 1, 2, 3 }, complete!.Versions.Select(v => v.VersionNo).OrderBy(n => n));
        Assert.Equal(1m, complete.Version(1)!.CostingLines.Single().Quantity);
        Assert.Equal(7m, complete.Version(3)!.CostingLines.Single().Quantity);
        Assert.Same(current, complete.CurrentVersion);
    }

    [Fact]
    public async Task Reopening_a_quote_loaded_with_its_current_version_only_numbers_the_revision_correctly()
    {
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var repository = new QuoteRepository(db);
            var quote = await repository.GetAsync(_quoteId);

            // Lapsed after its 30 days, so it can be reopened as a new version.
            Assert.True(quote!.ExpireIfLapsed(new DateOnly(2026, 11, 15)));
            var revision = quote.Reopen(Author, new DateOnly(2026, 11, 15));
            revision.ChangeQuantity(revision.CostingLines.Single(), 4m);
            await repository.SaveChangesAsync();
        }

        await using var check = new TechnoSurfacesDbContext(_options);
        var saved = await new QuoteRepository(check).GetWithAllVersionsAsync(_quoteId);

        Assert.Equal(new[] { 1, 2, 3, 4 }, saved!.Versions.Select(v => v.VersionNo).OrderBy(n => n));
        Assert.Equal(new[] { 1m, 2m, 3m, 4m },
            saved.Versions.OrderBy(v => v.VersionNo).Select(v => v.CostingLines.Single().Quantity));
        Assert.True(saved.Version(3)!.IsSealed);
        Assert.False(saved.Version(4)!.IsSealed);
    }
}
