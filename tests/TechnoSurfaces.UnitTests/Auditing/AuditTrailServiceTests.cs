using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Auditing;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Auditing;

/// <summary>The audit trail screen's filters, and US-19: an estimator sees every change to their quote.</summary>
public sealed class AuditTrailServiceTests : IAsyncLifetime
{
    private const string MdId = "md-user-id";
    private const string EstimatorId = "estimator-user-id";

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private AuditTrailService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync();

        _db.Users.AddRange(
            new AppUser { Id = MdId, UserName = "md", FullName = "Managing Director", Role = UserRole.ManagingDirector },
            new AppUser { Id = EstimatorId, UserName = "estimator", FullName = "Estimator One", Role = UserRole.Estimator });
        await _db.SaveChangesAsync();

        _service = new AuditTrailService(_db);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task AddAsync(string entity, string key, string property, string? oldValue, string? newValue,
        string userId, DateTime atUtc)
    {
        var entry = new AuditEntry(entity, key, property, oldValue, newValue, userId);
        _db.AuditEntries.Add(entry);
        _db.Entry(entry).Property(e => e.ChangedAtUtc).CurrentValue = atUtc;
        await _db.SaveChangesAsync();
    }

    private static readonly DateTime Noon = new(2026, 10, 2, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task Filters_by_user()
    {
        await AddAsync(nameof(RatePrice), "1", "Amount", "450.00", "495.00", MdId, Noon);
        await AddAsync(nameof(Quote), "1", "Status", "Draft", "PendingApproval", EstimatorId, Noon);

        var page = await _service.SearchAsync(new AuditFilter(UserId: EstimatorId));

        var row = Assert.Single(page.Rows);
        Assert.Equal("Estimator One", row.UserName);
    }

    [Fact]
    public async Task The_to_date_includes_the_whole_of_that_day_in_south_african_time()
    {
        // 21:59 UTC is 23:59 SAST on 3 October; 22:00 UTC is midnight, 4 October.
        await AddAsync(nameof(RatePrice), "1", "Amount", "1", "2", MdId, new DateTime(2026, 10, 3, 21, 59, 0, DateTimeKind.Utc));
        await AddAsync(nameof(RatePrice), "2", "Amount", "1", "2", MdId, new DateTime(2026, 10, 3, 22, 0, 0, DateTimeKind.Utc));

        var page = await _service.SearchAsync(new AuditFilter(From: new DateOnly(2026, 10, 3), To: new DateOnly(2026, 10, 3)));

        Assert.Equal("1", Assert.Single(page.Rows).EntityKey);
    }

    [Fact]
    public async Task Price_only_keeps_material_and_rate_price_changes()
    {
        await AddAsync(nameof(MaterialPrice), "1", "PricePerSqm", "100", "110", MdId, Noon);
        await AddAsync(nameof(RatePrice), "1", "Amount", "450.00", "495.00", MdId, Noon);
        await AddAsync(nameof(Quote), "1", "Status", "Draft", "PendingApproval", EstimatorId, Noon);

        var page = await _service.SearchAsync(new AuditFilter(PriceOnly: true));

        Assert.Equal(2, page.TotalMatching);
        Assert.All(page.Rows, r => Assert.True(r.IsPriceChange));
    }

    [Fact]
    public async Task Startup_work_is_shown_as_system()
    {
        await AddAsync(nameof(RatePrice), "1", "Amount", null, "450.00", ICurrentUser.SystemUserId, Noon);

        var page = await _service.SearchAsync(new AuditFilter());

        Assert.Equal("System", Assert.Single(page.Rows).UserName);
    }

    [Fact]
    public async Task A_quotes_history_includes_its_versions_and_lines_even_a_removed_line()
    {
        var customer = new Customer { Name = "Test Customer" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var contact = new Contact { CustomerId = customer.Id, FullName = "Test Contact" };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        var quote = new Quote("TS-TEST-1", customer.Id, contact.Id, EstimatorId, new DateOnly(2026, 10, 1));
        var version = quote.StartNewVersion(EstimatorId, 30m);
        var other = new Quote("TS-TEST-2", customer.Id, contact.Id, EstimatorId, new DateOnly(2026, 10, 1));
        other.StartNewVersion(EstimatorId, 30m);
        _db.Quotes.AddRange(quote, other);
        await _db.SaveChangesAsync();

        var v = version.Id.ToString();
        await AddAsync(nameof(Quote), quote.Id.ToString(), "Status", "Draft", "PendingApproval", EstimatorId, Noon);
        await AddAsync(nameof(QuoteVersion), v, "MarkupPercent", "30", "35", MdId, Noon.AddMinutes(1));

        // Line 9001 was added to this version and later removed; only its audit rows remain.
        await AddAsync(nameof(CostingLine), "9001", nameof(CostingLine.QuoteVersionId), null, v, EstimatorId, Noon.AddMinutes(2));
        await AddAsync(nameof(CostingLine), "9001", "(deleted)", null, null, MdId, Noon.AddMinutes(3));

        await AddAsync(nameof(Quote), other.Id.ToString(), "Status", "Draft", "PendingApproval", EstimatorId, Noon);

        var rows = await _service.ForQuoteAsync(quote.Id);

        Assert.Equal(4, rows.Count);
        Assert.True(rows[0].IsDeletion);                       // newest first
        Assert.Equal("Managing Director", rows[0].UserName);   // the estimator sees who changed it
        Assert.DoesNotContain(rows, r => r.EntityName == nameof(Quote) && r.EntityKey == other.Id.ToString());
    }

    [Fact]
    public async Task A_quotes_history_is_found_by_its_reference_and_an_unknown_reference_has_none()
    {
        var customer = new Customer { Name = "Reference Customer" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var contact = new Contact { CustomerId = customer.Id, FullName = "Reference Contact" };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        var quote = new Quote("TS-REF-1", customer.Id, contact.Id, EstimatorId, new DateOnly(2026, 10, 1));
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync();

        await AddAsync(nameof(Quote), quote.Id.ToString(), "Status", "Draft", "PendingApproval", EstimatorId, Noon);

        Assert.Single(await _service.ForQuoteReferenceAsync("TS-REF-1"));
        Assert.Empty(await _service.ForQuoteReferenceAsync("TS-DOES-NOT-EXIST"));
    }
}