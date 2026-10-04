using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Customers;

/// <summary>
/// The starting customer: RA Woodcraft, real, with no invented person attached.
/// </summary>
public sealed class CustomerSeederTests : IAsyncLifetime
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
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    [Fact]
    public async Task Only_the_real_customer_is_seeded_with_no_invented_person()
    {
        await using var db = new TechnoSurfacesDbContext(_options);

        await CustomerSeeder.SeedAsync(db);

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        Assert.Equal("RA Woodcraft", customer.Name);
        Assert.Equal(CustomerSeeder.AccountCode, customer.AccountCode);

        var contact = Assert.Single(customer.Contacts);
        Assert.Equal("Accounts", contact.FullName);
        Assert.Null(contact.Email);
        Assert.Null(contact.Phone);
    }

    [Fact]
    public async Task Seeding_again_adds_nothing()
    {
        await using (var db = new TechnoSurfacesDbContext(_options))
            await CustomerSeeder.SeedAsync(db);

        await using (var db = new TechnoSurfacesDbContext(_options))
            await CustomerSeeder.SeedAsync(db);

        await using var check = new TechnoSurfacesDbContext(_options);
        Assert.Equal(1, await check.Customers.CountAsync());
        Assert.Equal(1, await check.Contacts.CountAsync());
    }
}
