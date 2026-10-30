using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The names the customer quotation gives its materials, read from the seeded
/// catalogue and brands on SQLite: brand, product line, thickness and colour, for
/// every line in one query however many lines there are (NFR-05).
/// </summary>
public sealed class MaterialNameReaderTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private readonly CommandCounter _counter = new();

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).AddInterceptors(_counter).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await QuotationTermsSeeder.SeedAsync(db);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task A_colour_price_names_its_colour_and_a_band_price_lists_the_bands_colours()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var colourPrice = await db.MaterialPrices.Include(p => p.Colour).ThenInclude(c => c!.ProductLine).FirstAsync(p => p.ColourId != null);
        var bandPrice = await db.MaterialPrices.Include(p => p.PriceBand).ThenInclude(b => b!.Colours)
            .FirstAsync(p => p.PriceBand != null && p.PriceBand.ProductLine!.Name == "Staron");

        var names = await new MaterialNameReader(db).ForMaterialPricesAsync(new[] { colourPrice.Id, bandPrice.Id });

        var single = names[colourPrice.Id];
        Assert.Equal(colourPrice.Colour!.Name, single.Colour);
        Assert.Equal(colourPrice.Colour.ProductLine!.Name, single.ProductLine);
        Assert.Equal(colourPrice.Colour.ProductLine.ThicknessMm, single.ThicknessMm);
        Assert.Empty(single.BandColours);

        var band = names[bandPrice.Id];
        Assert.Equal("Staron", band.Brand);
        Assert.Null(band.Colour);
        Assert.Equal(bandPrice.PriceBand!.Colours.Select(c => c.Name).OrderBy(n => n), band.BandColours.OrderBy(n => n));
    }

    [Fact]
    public async Task Every_price_is_named_in_one_query()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var one = await db.MaterialPrices.OrderBy(p => p.Id).Select(p => p.Id).Take(1).ToListAsync();
        var many = await db.MaterialPrices.OrderBy(p => p.Id).Select(p => p.Id).Take(20).ToListAsync();
        var reader = new MaterialNameReader(db);

        _counter.Count = 0;
        await reader.ForMaterialPricesAsync(one);
        var forOne = _counter.Count;

        _counter.Count = 0;
        var named = await reader.ForMaterialPricesAsync(many);
        var forMany = _counter.Count;

        Assert.Equal(20, named.Count);
        Assert.Equal(1, forOne);
        Assert.Equal(forOne, forMany);
    }

    /// <summary>Counts the SQL commands sent.</summary>
    private sealed class CommandCounter : DbCommandInterceptor
    {
        public int Count { get; set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
