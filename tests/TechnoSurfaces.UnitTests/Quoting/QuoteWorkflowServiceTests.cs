using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The quote workflow on SQLite with the real seeded catalogue, rate card and terms:
/// create, submit, the approval queue, approve, send, accept, reopen and expiry
/// (US-14 to US-18, US-20, US-21). Who may do each step is the caller's policy check
/// and is covered by the integration tests.
/// </summary>
public sealed class QuoteWorkflowServiceTests : IAsyncLifetime
{
    private const string Md = "user-md";
    private const string Estimator = "user-estimator";

    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _customerId;
    private int _contactId;
    private int _sandingRateItemId;

    private readonly FixedTime _time = new(new DateTimeOffset(2026, 10, 4, 8, 0, 0, TimeSpan.Zero));

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await RateCardSeeder.SeedAsync(db);
        await QuotationTermsSeeder.SeedAsync(db);
        await CustomerSeeder.SeedAsync(db);

        db.Users.AddRange(
            new AppUser { Id = Md, UserName = "md", FullName = "Paul Schluter", Role = UserRole.ManagingDirector },
            new AppUser { Id = Estimator, UserName = "est", FullName = "Test Estimator", Role = UserRole.Estimator });
        await db.SaveChangesAsync();

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        _customerId = customer.Id;
        _contactId = customer.Contacts.Single().Id;
        _sandingRateItemId = await db.RateItems.Where(r => r.Name == "Sanding time").Select(r => r.Id).SingleAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private sealed class FixedTime : TimeProvider
    {
        public FixedTime(DateTimeOffset now) => Now = now;
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class SignedIn : ICurrentUser
    {
        public SignedIn(string userId) => UserId = userId;
        public string UserId { get; }
    }

    private DateOnly Today => BusinessDate.Today(_time);

    private async Task<T> AsAsync<T>(string userId, Func<QuoteWorkflowService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var calculator = new QuoteCalculationService();
        var queries = new QuoteQueries(db, calculator);
        var service = new QuoteWorkflowService(
            new QuoteRepository(db), queries, queries, new CustomerRepository(db),
            new QuoteTermsRecorder(new QuotationTermsReader(db)), calculator, new SignedIn(userId), _time);
        return await act(service);
    }

    private async Task<int> CreateAsync(string userId, string reference = "TS-WF-1")
    {
        var result = await AsAsync(userId, s => s.CreateAsync(new NewQuote(reference, _customerId, _contactId, 47m,
            Site: "Tokai", Project: "Kitchen")));
        Assert.Equal(WorkflowOutcome.Ok, result.Outcome);
        return result.Quote!.Id;
    }

    /// <summary>
    /// Prices the quote at 3 hours of sanding (R300, R441 with 47% markup) and writes
    /// the customer quotation line to match, as an estimator would before submitting.
    /// </summary>
    private async Task AddLineAsync(int quoteId, bool withQuotationLine = true)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var quote = await new QuoteRepository(db).GetAsync(quoteId);
        var version = quote!.CurrentVersion!;
        version.AddCostingLine(
            CostingLine.ForRate(_sandingRateItemId, "Sanding time", 100m, "Rate card: Sanding time", 3m, isBelowTheLine: false));
        if (withQuotationLine)
            version.AddQuotationLine(new QuotationLine("Kitchen countertop, fabricate and install", 441m, "Kitchen"));
        await db.SaveChangesAsync();
    }

    private async Task<Quote> ReadAsync(int quoteId)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return (await db.Quotes
            .Include(q => q.Versions).ThenInclude(v => v.Terms)
            .Include(q => q.Versions).ThenInclude(v => v.CostingLines)
            .AsSplitQuery()
            .SingleAsync(q => q.Id == quoteId));
    }

    [Fact]
    public async Task A_new_quote_starts_as_a_draft_valid_for_thirty_days()
    {
        var created = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote(" TS-WF-2 ", _customerId, _contactId, 47m,
            Site: " Tokai ", Project: "Kitchen", CustomerReference: "SWEET VALLEY FARM")));

        var quote = created.Quote!;
        Assert.Equal("TS-WF-2", quote.Reference);
        Assert.Equal("Draft", quote.Status);
        Assert.Equal("Tokai", quote.Site);
        Assert.Equal("SWEET VALLEY FARM", quote.CustomerReference);
        Assert.Equal(Today, quote.IssueDate);
        Assert.Equal(Today.AddDays(30), quote.ValidUntil);
        Assert.Equal("Test Estimator", quote.AuthorName);
        Assert.Equal(1, quote.VersionNo);
        Assert.Equal(47m, quote.Totals.MarkupPercent);
    }

    [Fact]
    public async Task A_quote_can_be_given_its_own_validity_period()
    {
        var created = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("TS-VAL-1", _customerId, _contactId, 47m,
            ValidForDays: 60)));

        Assert.Equal(Today.AddDays(60), created.Quote!.ValidUntil);
        Assert.Equal(60, created.Quote.DaysRemaining);
    }

    [Fact]
    public async Task A_validity_period_outside_one_day_to_a_year_is_refused()
    {
        var none = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("TS-VAL-2", _customerId, _contactId, 47m, ValidForDays: 0)));
        var tooLong = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("TS-VAL-3", _customerId, _contactId, 47m, ValidForDays: 366)));

        Assert.Equal(WorkflowOutcome.Invalid, none.Outcome);
        Assert.Contains(nameof(NewQuote.ValidForDays), none.Errors!.Keys);
        Assert.Equal(WorkflowOutcome.Invalid, tooLong.Outcome);
    }

    [Fact]
    public async Task A_reference_already_in_use_is_refused()
    {
        await CreateAsync(Estimator, "TS-DUP");

        var second = await AsAsync(Md, s => s.CreateAsync(new NewQuote("TS-DUP", _customerId, _contactId, 40m)));

        Assert.Equal(WorkflowOutcome.ReferenceTaken, second.Outcome);
    }

    [Fact]
    public async Task A_contact_must_belong_to_the_customer_and_both_be_active()
    {
        int otherContactId;
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var other = new Customer { Name = "Other", Contacts = { new Contact { FullName = "Other contact" } } };
            db.Customers.Add(other);
            await db.SaveChangesAsync();
            otherContactId = other.Contacts.Single().Id;
        }

        var wrongContact = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("TS-C1", _customerId, otherContactId, 40m)));
        Assert.Equal(WorkflowOutcome.Invalid, wrongContact.Outcome);
        Assert.Contains(nameof(NewQuote.ContactId), wrongContact.Errors!.Keys);

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            (await db.Customers.FindAsync(_customerId))!.IsActive = false;
            await db.SaveChangesAsync();
        }

        var inactive = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("TS-C2", _customerId, _contactId, 40m)));
        Assert.Contains(nameof(NewQuote.CustomerId), inactive.Errors!.Keys);
    }

    [Fact]
    public async Task An_empty_reference_and_a_negative_markup_are_refused()
    {
        var result = await AsAsync(Estimator, s => s.CreateAsync(new NewQuote("  ", _customerId, _contactId, -1m)));

        Assert.Equal(WorkflowOutcome.Invalid, result.Outcome);
        Assert.Contains(nameof(NewQuote.Reference), result.Errors!.Keys);
        Assert.Contains(nameof(NewQuote.MarkupPercent), result.Errors.Keys);
    }

    [Fact]
    public async Task A_submitted_quote_appears_in_the_approval_queue_with_its_value()
    {
        var id = await CreateAsync(Estimator);
        await AddLineAsync(id);

        var submitted = await AsAsync(Estimator, s => s.SubmitAsync(id));
        var queue = await AsAsync(Md, s => s.ApprovalQueueAsync());
        var pending = await AsAsync(Md, s => s.PendingCountAsync());

        Assert.Equal("PendingApproval", submitted.Quote!.Status);
        var row = Assert.Single(queue);
        Assert.Equal(id, row.Id);
        Assert.Equal("RA Woodcraft", row.CustomerName);
        Assert.Equal("Kitchen", row.Project);
        Assert.Equal(441m, row.TotalExVat); // 3 h x R100 = R300, plus 47% markup
        Assert.Equal(1, pending);
    }

    [Fact]
    public async Task A_quote_with_no_lines_cannot_be_submitted()
    {
        var id = await CreateAsync(Estimator);

        var result = await AsAsync(Estimator, s => s.SubmitAsync(id));

        Assert.Equal(WorkflowOutcome.NotAllowed, result.Outcome);
        Assert.Equal(QuoteStatus.Draft, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task Approving_records_the_terms_and_seals_the_version()
    {
        var id = await CreateAsync(Estimator);
        await AddLineAsync(id);
        await AsAsync(Estimator, s => s.SubmitAsync(id));

        var approved = await AsAsync(Md, s => s.ApproveAsync(id));

        Assert.Equal("Approved", approved.Quote!.Status);
        Assert.Equal("Paul Schluter", approved.Quote.ApprovedByName);
        var version = (await ReadAsync(id)).CurrentVersion!;
        Assert.True(version.IsSealed);
        Assert.NotEmpty(version.Terms);
    }

    [Fact]
    public async Task A_quote_is_not_approved_until_the_quotation_adds_up_to_the_costing()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id, withQuotationLine: false);

        var noLines = await AsAsync(Md, s => s.ApproveAsync(id));

        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(id);
            quote!.CurrentVersion!.AddQuotationLine(new QuotationLine("Kitchen", 400m));
            await db.SaveChangesAsync();
        }

        var mismatch = await AsAsync(Md, s => s.ApproveAsync(id));

        Assert.Equal(WorkflowOutcome.NotAllowed, noLines.Outcome);
        Assert.Equal(WorkflowOutcome.NotAllowed, mismatch.Outcome);
        Assert.Contains("R400.00", mismatch.Problem);
        Assert.Contains("R441.00", mismatch.Problem);
        Assert.Equal(QuoteStatus.Draft, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task The_managing_directors_own_quote_is_approved_without_the_queue()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id);

        var approved = await AsAsync(Md, s => s.ApproveAsync(id));

        Assert.Equal("Approved", approved.Quote!.Status);
    }

    [Fact]
    public async Task An_estimators_draft_cannot_be_approved_without_being_submitted()
    {
        var id = await CreateAsync(Estimator);
        await AddLineAsync(id);

        var result = await AsAsync(Md, s => s.ApproveAsync(id));

        Assert.Equal(WorkflowOutcome.NotAllowed, result.Outcome);
        var quote = await ReadAsync(id);
        Assert.Equal(QuoteStatus.Draft, quote.Status);
        Assert.Empty(quote.CurrentVersion!.Terms);
    }

    [Fact]
    public async Task A_quote_is_sent_then_accepted_and_cannot_be_accepted_before_it_is_sent()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id);
        await AsAsync(Md, s => s.ApproveAsync(id));

        var early = await AsAsync(Md, s => s.MarkAcceptedAsync(id));
        var sent = await AsAsync(Estimator, s => s.MarkSentAsync(id));
        var accepted = await AsAsync(Md, s => s.MarkAcceptedAsync(id));

        Assert.Equal(WorkflowOutcome.NotAllowed, early.Outcome);
        Assert.Equal("Sent", sent.Quote!.Status);
        Assert.Equal("Accepted", accepted.Quote!.Status);
    }

    [Fact]
    public async Task Reopening_a_sent_quote_creates_version_two_valid_for_thirty_days_from_today()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id);
        await AsAsync(Md, s => s.ApproveAsync(id));
        await AsAsync(Md, s => s.MarkSentAsync(id));
        _time.Now = _time.Now.AddDays(20);

        var reopened = await AsAsync(Estimator, s => s.ReopenAsync(id));
        var versions = await AsAsync(Md, s => s.VersionsAsync(id));

        Assert.Equal("Draft", reopened.Quote!.Status);
        Assert.Equal(2, reopened.Quote.VersionNo);
        Assert.Equal(Today.AddDays(30), reopened.Quote.ValidUntil);
        Assert.Null(reopened.Quote.ApprovedByName);
        Assert.Equal(2, versions!.Count);
        Assert.True(versions[0].IsSealed);
        Assert.False(versions[1].IsSealed);
        Assert.Equal(versions[0].TotalExVat, versions[1].TotalExVat);
    }

    [Fact]
    public async Task A_lapsed_quote_expires_when_quotes_are_listed_and_can_then_be_reopened()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id);
        await AsAsync(Md, s => s.ApproveAsync(id));
        await AsAsync(Md, s => s.MarkSentAsync(id));

        _time.Now = _time.Now.AddDays(31);
        var list = await AsAsync(Md, s => s.ListAsync(new QuoteListFilter()));

        Assert.Equal("Expired", list.Single(q => q.Id == id).Status);

        var reopened = await AsAsync(Md, s => s.ReopenAsync(id));
        Assert.Equal("Draft", reopened.Quote!.Status);
        Assert.Equal(Today.AddDays(30), reopened.Quote.ValidUntil);
    }

    [Fact]
    public async Task A_lapsed_quote_cannot_be_sent_or_accepted()
    {
        var id = await CreateAsync(Md);
        await AddLineAsync(id);
        await AsAsync(Md, s => s.ApproveAsync(id));
        await AsAsync(Md, s => s.MarkSentAsync(id));
        _time.Now = _time.Now.AddDays(31);

        var result = await AsAsync(Md, s => s.MarkAcceptedAsync(id));

        Assert.Equal(WorkflowOutcome.NotAllowed, result.Outcome);
        Assert.Equal(QuoteStatus.Expired, (await ReadAsync(id)).Status);
    }

    [Fact]
    public async Task A_quote_in_its_last_seven_days_is_flagged()
    {
        var id = await CreateAsync(Estimator);

        _time.Now = _time.Now.AddDays(22);
        var early = (await AsAsync(Estimator, s => s.GetAsync(id))).Quote!;
        _time.Now = _time.Now.AddDays(1);
        var late = (await AsAsync(Estimator, s => s.GetAsync(id))).Quote!;

        Assert.Equal(8, early.DaysRemaining);
        Assert.False(early.ExpiresSoon);
        Assert.Equal(7, late.DaysRemaining);
        Assert.True(late.ExpiresSoon);
    }

    [Fact]
    public async Task The_list_filters_by_author_status_and_search()
    {
        await CreateAsync(Estimator, "TS-EST-1");
        await CreateAsync(Md, "TS-MD-1");

        var mine = await AsAsync(Estimator, s => s.ListAsync(new QuoteListFilter(AuthorId: Estimator)));
        var drafts = await AsAsync(Md, s => s.ListAsync(new QuoteListFilter(Status: "Draft")));
        var search = await AsAsync(Md, s => s.ListAsync(new QuoteListFilter(Search: "MD-1")));

        Assert.Equal("TS-EST-1", Assert.Single(mine).Reference);
        Assert.Equal(2, drafts.Count);
        Assert.Equal("TS-MD-1", Assert.Single(search).Reference);
    }

    [Fact]
    public async Task Details_can_be_corrected_while_pending_but_not_once_approved()
    {
        var id = await CreateAsync(Estimator);
        await AddLineAsync(id);
        await AsAsync(Estimator, s => s.SubmitAsync(id));

        var corrected = await AsAsync(Md, s => s.UpdateDetailsAsync(id, new QuoteDetailsInput("Constantia", "Kitchen and scullery", null, null)));
        await AsAsync(Md, s => s.ApproveAsync(id));
        var afterApproval = await AsAsync(Md, s => s.UpdateDetailsAsync(id, new QuoteDetailsInput("Elsewhere", null, null, null)));

        Assert.Equal("Constantia", corrected.Quote!.Site);
        Assert.Equal(WorkflowOutcome.NotAllowed, afterApproval.Outcome);
        Assert.Equal("Constantia", (await ReadAsync(id)).Site);
    }

    [Fact]
    public async Task An_unknown_quote_is_not_found()
    {
        Assert.Equal(WorkflowOutcome.NotFound, (await AsAsync(Md, s => s.GetAsync(999))).Outcome);
        Assert.Equal(WorkflowOutcome.NotFound, (await AsAsync(Md, s => s.ApproveAsync(999))).Outcome);
        Assert.Null(await AsAsync(Md, s => s.VersionsAsync(999)));
    }
}
