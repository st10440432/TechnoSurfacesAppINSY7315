using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The standing quotation content (US-12) and the warranty that follows the brand
/// quoted (US-13), seeded from the client's quotation template on top of the real
/// catalogue.
/// </summary>
public sealed class QuotationTermsTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private IQuotationTermsReader _reader = null!;

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
        await QuotationTermsSeeder.SeedAsync(_db);

        _reader = new QuotationTermsReader(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task Every_section_of_the_template_is_seeded()
    {
        var terms = await _reader.GetStandingTermsAsync();

        Assert.Equal(1, terms.Count(t => t.Section == TermSection.Notes));
        Assert.Equal(2, terms.Count(t => t.Section == TermSection.LeadTimes));
        Assert.Equal(3, terms.Count(t => t.Section == TermSection.Exclusions));
        Assert.Equal(7, terms.Count(t => t.Section == TermSection.TermsAndConditions));
        Assert.Equal(4, terms.Count(t => t.Section == TermSection.Disclaimers));
        Assert.Equal(1, terms.Count(t => t.Section == TermSection.Warranties));
        Assert.Equal(1, terms.Count(t => t.Section == TermSection.PaymentTerms));
    }

    [Fact]
    public async Task Terms_come_back_by_section_and_in_template_order()
    {
        var terms = await _reader.GetStandingTermsAsync();

        Assert.Equal(terms.OrderBy(t => t.Section).Select(t => t.Section), terms.Select(t => t.Section));

        var conditions = terms.Where(t => t.Section == TermSection.TermsAndConditions).ToList();
        Assert.Equal("Quotation valid for 30 days only", conditions[0].Text);
        Assert.Equal("No contra charges, set off or retentions unless agreed in writing beforehand", conditions[^1].Text);
    }

    [Fact]
    public async Task A_retired_term_no_longer_appears_on_the_quotation()
    {
        var exclusion = await _db.QuotationTerms.FirstAsync(t => t.Text == "Removal of existing worktops");
        exclusion.IsActive = false;
        await _db.SaveChangesAsync();

        var terms = await _reader.GetStandingTermsAsync();

        Assert.DoesNotContain(terms, t => t.Text == "Removal of existing worktops");
        Assert.Equal(2, terms.Count(t => t.Section == TermSection.Exclusions));
    }

    [Fact]
    public async Task No_banking_details_are_seeded()
    {
        var terms = await _reader.GetStandingTermsAsync();

        Assert.DoesNotContain(terms, t => t.Section == TermSection.BankDetails);
        Assert.DoesNotContain(terms, t => t.Text.Contains("ABSA", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(terms, t => t.Text.Contains("account", StringComparison.OrdinalIgnoreCase)
                                       && t.Text.Contains("branch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_staron_product_line_carries_the_samsung_staron_warranty()
    {
        var staron = await _db.ProductLines.FirstAsync(p => p.Name == "Staron" && p.ThicknessMm == 12);

        var warranty = await _reader.GetWarrantyForProductLineAsync(staron.Id);

        Assert.NotNull(warranty);
        Assert.Equal(QuotationTermsSeeder.StaronBrand, warranty.Brand);
        Assert.Equal("10 years", warranty.MaterialWarranty);
        Assert.Equal("1 year", warranty.WorkmanshipWarranty);
    }

    [Fact]
    public async Task Every_staron_product_line_is_linked_and_no_other_line_is()
    {
        var lines = await _db.ProductLines.AsNoTracking().Include(p => p.Brand).ToListAsync();

        Assert.All(lines.Where(p => p.Name == "Staron"), p => Assert.Equal(QuotationTermsSeeder.StaronBrand, p.Brand?.Name));
        Assert.All(lines.Where(p => p.Name != "Staron"), p => Assert.Null(p.BrandId));
    }

    [Fact]
    public async Task A_product_line_with_no_confirmed_brand_has_no_warranty()
    {
        var maxPure = await _db.ProductLines.FirstAsync(p => p.Name == "Max Pure Solid Surface" && p.ThicknessMm == 12);

        Assert.Null(await _reader.GetWarrantyForProductLineAsync(maxPure.Id));
    }

    [Fact]
    public async Task A_brand_whose_warranty_is_not_confirmed_gives_no_warranty()
    {
        var brand = new Brand { Name = "Unconfirmed brand" };
        _db.Brands.Add(brand);
        var line = await _db.ProductLines.FirstAsync(p => p.Name == "Perago 100% Acrylic" && p.ThicknessMm == 12);
        line.Brand = brand;
        await _db.SaveChangesAsync();

        Assert.Null(await _reader.GetWarrantyForProductLineAsync(line.Id));
    }

    [Fact]
    public async Task The_database_refuses_half_a_warranty()
    {
        _db.Brands.Add(new Brand { Name = "Half warranty", MaterialWarranty = "10 years" });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task The_database_refuses_a_blank_term()
    {
        _db.QuotationTerms.Add(new Domain.Quoting.QuotationTerm { Section = TermSection.Notes, Text = "", SortOrder = 99 });

        await Assert.ThrowsAsync<DbUpdateException>(() => _db.SaveChangesAsync());
    }

    [Fact]
    public async Task Seeding_twice_does_not_duplicate_the_terms_or_the_brands()
    {
        var terms = await _db.QuotationTerms.CountAsync();
        var brands = await _db.Brands.CountAsync();

        await QuotationTermsSeeder.SeedAsync(_db);

        Assert.Equal(terms, await _db.QuotationTerms.CountAsync());
        Assert.Equal(brands, await _db.Brands.CountAsync());
    }
}
