using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// Recording the Sage Pastel invoice against an accepted quote (US-25): only an
/// accepted quote, one invoice per quote, one quote per invoice number.
/// </summary>
public sealed class InvoiceRecordServiceTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _customerId;
    private int _contactId;

    private static readonly DateOnly IssueDate = new(2026, 10, 1);

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 4, 8, 0, 0, TimeSpan.Zero);
    }

    private sealed class SignedIn : ICurrentUser
    {
        public string UserId => "user-md";
    }

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
        _customerId = customer.Id;
        _contactId = customer.Contacts.Single().Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    /// <summary>A quote at R1 000 ex VAT (R1 150 inc VAT), taken as far as the given status.</summary>
    private async Task<int> QuoteAsync(string reference, QuoteStatus upTo)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var rateItemId = await db.RateItems.Select(r => r.Id).FirstAsync();
        var quote = new Quote(reference, _customerId, _contactId, "user-md", IssueDate);
        var version = quote.StartNewVersion("user-md", markupPercent: 0m);
        version.AddCostingLine(CostingLine.ForRate(rateItemId, "Labour", 100m, "Rate card", 10m, isBelowTheLine: false));

        if (upTo >= QuoteStatus.Approved)
            quote.Approve("user-md");
        if (upTo >= QuoteStatus.Sent)
            quote.MarkSent();
        if (upTo >= QuoteStatus.Accepted)
            quote.MarkAccepted();

        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return quote.Id;
    }

    private async Task<InvoiceResult> RecordAsync(int quoteId, InvoiceInput input)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var service = new InvoiceRecordService(new QuoteRepository(db), new InvoiceRecords(db),
            new QuoteCalculationService(), new SignedIn(), new FixedTime());
        return await service.RecordAsync(quoteId, input);
    }

    private async Task<InvoiceResult> GetAsync(int quoteId)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var service = new InvoiceRecordService(new QuoteRepository(db), new InvoiceRecords(db),
            new QuoteCalculationService(), new SignedIn(), new FixedTime());
        return await service.GetAsync(quoteId);
    }

    [Fact]
    public async Task An_invoice_is_recorded_against_an_accepted_quote_with_its_variance()
    {
        var id = await QuoteAsync("TS-INV-1", QuoteStatus.Accepted);

        var recorded = await RecordAsync(id, new InvoiceInput(" IN114317 ", new DateOnly(2026, 10, 3), 1200m));
        var read = await GetAsync(id);

        Assert.Equal(InvoiceOutcome.Ok, recorded.Outcome);
        Assert.Equal("IN114317", read.Invoice!.InvoiceNumber);
        Assert.Equal(1150m, read.Invoice.QuotedTotalIncVat);
        Assert.Equal(50m, read.Invoice.Variance);
        Assert.Equal("user-md", read.Invoice.RecordedByUserId);
    }

    [Fact]
    public async Task A_quote_that_is_not_accepted_takes_no_invoice()
    {
        var id = await QuoteAsync("TS-INV-2", QuoteStatus.Sent);

        var result = await RecordAsync(id, new InvoiceInput("IN1", new DateOnly(2026, 10, 3), 100m));

        Assert.Equal(InvoiceOutcome.Conflict, result.Outcome);
        Assert.Equal(InvoiceOutcome.NotRecorded, (await GetAsync(id)).Outcome);
    }

    [Fact]
    public async Task A_quote_takes_one_invoice_and_an_invoice_number_one_quote()
    {
        var first = await QuoteAsync("TS-INV-3", QuoteStatus.Accepted);
        var second = await QuoteAsync("TS-INV-4", QuoteStatus.Accepted);
        await RecordAsync(first, new InvoiceInput("IN200", new DateOnly(2026, 10, 3), 100m));

        var again = await RecordAsync(first, new InvoiceInput("IN201", new DateOnly(2026, 10, 3), 100m));
        var reused = await RecordAsync(second, new InvoiceInput("IN200", new DateOnly(2026, 10, 3), 100m));

        Assert.Equal(InvoiceOutcome.Conflict, again.Outcome);
        Assert.Equal(InvoiceOutcome.Conflict, reused.Outcome);
    }

    [Fact]
    public async Task Missing_number_zero_amount_and_impossible_dates_are_refused()
    {
        var id = await QuoteAsync("TS-INV-5", QuoteStatus.Accepted);

        var blank = await RecordAsync(id, new InvoiceInput(" ", new DateOnly(2026, 10, 3), 0m));
        var future = await RecordAsync(id, new InvoiceInput("IN300", new DateOnly(2026, 10, 5), 100m));
        var beforeIssue = await RecordAsync(id, new InvoiceInput("IN300", new DateOnly(2026, 9, 30), 100m));

        Assert.Equal(InvoiceOutcome.Invalid, blank.Outcome);
        Assert.Contains(nameof(InvoiceInput.InvoiceNumber), blank.Errors!.Keys);
        Assert.Contains(nameof(InvoiceInput.AmountIncVat), blank.Errors.Keys);
        Assert.Contains(nameof(InvoiceInput.InvoiceDate), future.Errors!.Keys);
        Assert.Contains(nameof(InvoiceInput.InvoiceDate), beforeIssue.Errors!.Keys);
    }

    [Fact]
    public async Task An_unknown_quote_is_not_found()
    {
        Assert.Equal(InvoiceOutcome.QuoteNotFound, (await GetAsync(999)).Outcome);
        Assert.Equal(InvoiceOutcome.QuoteNotFound,
            (await RecordAsync(999, new InvoiceInput("IN9", new DateOnly(2026, 10, 3), 1m))).Outcome);
    }
}
