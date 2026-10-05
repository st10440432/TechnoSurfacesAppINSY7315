using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// An approved version keeps the standing wording and the brand warranties it was
/// approved with, so a quotation issued last month still reads as it did when the
/// customer signed it, after the Managing Director changes the terms (US-21, US-22).
/// </summary>
public sealed class QuoteTermsRecordingTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;

    private const string ManagingDirector = "managing-director";

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

    private static IQuoteTermsRecorder Recorder(TechnoSurfacesDbContext db) =>
        new QuoteTermsRecorder(new QuotationTermsReader(db));

    private static Task<int> StaronPriceIdAsync(TechnoSurfacesDbContext db) =>
        db.MaterialPrices
            .Where(p => p.PriceBand != null && p.PriceBand.ProductLine!.Name == "Staron")
            .Select(p => p.Id)
            .FirstAsync();

    private static Task<int> MaxOnTopPriceIdAsync(TechnoSurfacesDbContext db) =>
        db.MaterialPrices
            .Where(p => p.Colour != null && p.Colour.ProductLine!.Name == "Max Pure Solid Surface")
            .Select(p => p.Id)
            .FirstAsync();

    /// <summary>Creates and saves a Managing Director's draft with one material line per price.</summary>
    private static async Task<Quote> DraftAsync(TechnoSurfacesDbContext db, string reference, params int[] materialPriceIds)
    {
        var customer = new Customer { Name = reference };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();
        var contact = new Contact { CustomerId = customer.Id, FullName = "Contact" };
        db.Contacts.Add(contact);
        await db.SaveChangesAsync();

        var quote = new Quote(reference, customer.Id, contact.Id, ManagingDirector, new DateOnly(2026, 10, 3));
        var version = quote.StartNewVersion(ManagingDirector, markupPercent: 47m);
        foreach (var priceId in materialPriceIds)
            version.AddCostingLine(CostingLine.ForMaterial(priceId, "Material", 4335.04m, "origin", 1m, 2.7968m));

        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return quote;
    }

    [Fact]
    public async Task Approving_records_every_active_term_and_the_warranty_of_the_brand_quoted()
    {
        int quoteId;
        int activeTerms;

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            activeTerms = await db.QuotationTerms.CountAsync(t => t.IsActive);
            var quote = await DraftAsync(db, "TS-TERMS-1", await StaronPriceIdAsync(db));

            await Recorder(db).RecordAsync(quote.CurrentVersion!);
            quote.Approve(ManagingDirector);
            await db.SaveChangesAsync();
            quoteId = quote.Id;
        }

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var version = await db.QuoteVersions
                .Include(v => v.Terms).Include(v => v.Warranties)
                .SingleAsync(v => v.QuoteId == quoteId);

            Assert.True(version.IsSealed);
            Assert.Equal(activeTerms, version.Terms.Count);

            var warranty = Assert.Single(version.Warranties);
            Assert.Equal("Staron", warranty.Brand);
            Assert.Equal("10 years", warranty.MaterialWarranty);
            Assert.Equal("1 year", warranty.WorkmanshipWarranty);
        }
    }

    [Fact]
    public async Task Changing_the_terms_later_does_not_change_an_approved_version()
    {
        int quoteId;

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await DraftAsync(db, "TS-TERMS-2", await StaronPriceIdAsync(db));
            await Recorder(db).RecordAsync(quote.CurrentVersion!);
            quote.Approve(ManagingDirector);
            await db.SaveChangesAsync();
            quoteId = quote.Id;
        }

        // The Managing Director rewords the validity term, retires an exclusion and
        // changes the Staron workmanship warranty.
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var validity = await db.QuotationTerms.SingleAsync(t => t.Text == "Quotation valid for 30 days only");
            validity.Text = "Quotation valid for 14 days only";
            var exclusion = await db.QuotationTerms.SingleAsync(t => t.Text == "Removal of existing worktops");
            exclusion.IsActive = false;
            var staron = await db.Brands.SingleAsync(b => b.Name == "Staron");
            staron.WorkmanshipWarranty = "2 years";
            await db.SaveChangesAsync();
        }

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var version = await db.QuoteVersions
                .Include(v => v.Terms).Include(v => v.Warranties)
                .SingleAsync(v => v.QuoteId == quoteId);

            Assert.Contains(version.Terms, t => t.Text == "Quotation valid for 30 days only");
            Assert.DoesNotContain(version.Terms, t => t.Text == "Quotation valid for 14 days only");
            Assert.Contains(version.Terms, t => t.Text == "Removal of existing worktops");
            Assert.Equal("1 year", version.Warranties.Single().WorkmanshipWarranty);
        }
    }

    [Fact]
    public async Task Material_from_a_brand_with_no_confirmed_warranty_records_no_warranty()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await DraftAsync(db, "TS-TERMS-3", await MaxOnTopPriceIdAsync(db));

        await Recorder(db).RecordAsync(quote.CurrentVersion!);

        Assert.True(quote.CurrentVersion!.HasRecordedTerms);
        Assert.Empty(quote.CurrentVersion.Warranties);
    }

    [Fact]
    public async Task A_quote_mixing_brands_records_only_the_confirmed_warranty()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await DraftAsync(db, "TS-TERMS-4", await StaronPriceIdAsync(db), await MaxOnTopPriceIdAsync(db));

        await Recorder(db).RecordAsync(quote.CurrentVersion!);

        Assert.Equal("Staron", Assert.Single(quote.CurrentVersion!.Warranties).Brand);
    }

    [Fact]
    public async Task Terms_cannot_be_recorded_twice()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await DraftAsync(db, "TS-TERMS-5", await StaronPriceIdAsync(db));
        await Recorder(db).RecordAsync(quote.CurrentVersion!);

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder(db).RecordAsync(quote.CurrentVersion!));
    }

    [Fact]
    public async Task Terms_cannot_be_recorded_on_a_sealed_version()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await DraftAsync(db, "TS-TERMS-6", await StaronPriceIdAsync(db));
        quote.CurrentVersion!.Seal();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Recorder(db).RecordAsync(quote.CurrentVersion!));
    }

    [Fact]
    public async Task Bank_details_entered_by_the_managing_director_are_recorded_on_the_next_approval()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        db.QuotationTerms.Add(new QuotationTerm { Section = TermSection.BankDetails, Text = "Bank details as entered", SortOrder = 1 });
        await db.SaveChangesAsync();

        var quote = await DraftAsync(db, "TS-TERMS-7", await StaronPriceIdAsync(db));
        await Recorder(db).RecordAsync(quote.CurrentVersion!);

        Assert.Contains(quote.CurrentVersion!.Terms, t => t.Section == TermSection.BankDetails && t.Text == "Bank details as entered");
    }
}
