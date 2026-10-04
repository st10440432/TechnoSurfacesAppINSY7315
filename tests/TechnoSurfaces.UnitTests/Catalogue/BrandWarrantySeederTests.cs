using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Catalogue;

/// <summary>
/// Team decision of 4 October: every brand except DuPont Corian is quoted with the
/// Staron and Avonite wording, 10 years material and 1 year workmanship (US-13).
/// </summary>
public sealed class BrandWarrantySeederTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await QuotationTermsSeeder.SeedAsync(db);
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Every_product_line_ends_up_with_a_brand_and_a_warranty()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        await BrandWarrantySeeder.SeedAsync(db);

        var lines = await db.ProductLines.Include(p => p.Brand).ToListAsync();
        Assert.All(lines, l =>
        {
            Assert.NotNull(l.Brand);
            Assert.True(l.Brand!.HasConfirmedWarranty, $"{l.Name} has no warranty");
        });
        Assert.All(lines.Where(l => !l.Name.StartsWith("Staron")), l =>
        {
            Assert.Equal(BrandWarrantySeeder.MaterialWarranty, l.Brand!.MaterialWarranty);
            Assert.Equal(BrandWarrantySeeder.WorkmanshipWarranty, l.Brand.WorkmanshipWarranty);
        });
    }

    [Fact]
    public async Task Staron_and_corian_keep_their_own_wording()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        await BrandWarrantySeeder.SeedAsync(db);

        var corian = await db.Brands.SingleAsync(b => b.Name == "DuPont Corian");
        Assert.Equal("10 years (limited)", corian.WorkmanshipWarranty);
        Assert.All(await db.ProductLines.Include(p => p.Brand).Where(p => p.Name == "Staron").ToListAsync(),
            l => Assert.NotEqual("Infinito", l.Brand!.Name));
    }

    [Fact]
    public async Task Running_again_adds_nothing_and_changes_nothing_the_managing_director_set()
    {
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            await BrandWarrantySeeder.SeedAsync(db);
            var infinito = await db.Brands.SingleAsync(b => b.Name == "Infinito");
            infinito.WorkmanshipWarranty = "2 years";
            await db.SaveChangesAsync();
        }

        int brandsBefore;
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            brandsBefore = await db.Brands.CountAsync();
            await BrandWarrantySeeder.SeedAsync(db);
        }

        await using var check = new TechnoSurfacesDbContext(_options);
        Assert.Equal(brandsBefore, await check.Brands.CountAsync());
        Assert.Equal("2 years", (await check.Brands.SingleAsync(b => b.Name == "Infinito")).WorkmanshipWarranty);
    }
}
