using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Auditing;

/// <summary>
/// NFR-04: every price change is recorded with the user, the time, and the old
/// and new value. One of the security tests in the build plan.
/// </summary>
public sealed class AuditInterceptorTests : IAsyncLifetime
{
    private const string ManagingDirectorId = "md-user-id";

    private sealed class FixedUser : ICurrentUser
    {
        public string UserId => ManagingDirectorId;
    }

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new AuditInterceptor(new FixedUser()))
                .Options);

        await _db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<RatePrice> AddRatePriceAsync(decimal amount)
    {
        var item = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        _db.RateItems.Add(item);
        await _db.SaveChangesAsync();

        var price = new RatePrice { RateItemId = item.Id, Amount = amount, EffectiveFrom = new DateOnly(2026, 1, 1) };
        _db.RatePrices.Add(price);
        await _db.SaveChangesAsync();

        return price;
    }

    [Fact]
    public async Task Changing_a_price_records_the_old_value_new_value_user_and_time()
    {
        var price = await AddRatePriceAsync(450.00m);
        var before = DateTime.UtcNow;

        price.Amount = 495.00m;
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) &&
            a.PropertyName == nameof(RatePrice.Amount) &&
            a.OldValue != null);

        Assert.Equal(price.Id.ToString(), entry.EntityKey);
        Assert.Equal("450.00", entry.OldValue);
        Assert.Equal("495.00", entry.NewValue);
        Assert.Equal(ManagingDirectorId, entry.UserId);
        Assert.InRange(entry.ChangedAtUtc, before, DateTime.UtcNow);
    }

    [Fact]
    public async Task A_new_price_is_recorded_against_its_real_key()
    {
        var price = await AddRatePriceAsync(450.00m);

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) && a.PropertyName == nameof(RatePrice.Amount));

        Assert.Equal(price.Id.ToString(), entry.EntityKey);
        Assert.Null(entry.OldValue);
        Assert.Equal("450.00", entry.NewValue);
    }

    [Fact]
    public async Task Entities_outside_the_audited_set_are_not_recorded()
    {
        await AddRatePriceAsync(450.00m);

        Assert.False(await _db.AuditEntries.AnyAsync(a => a.EntityName == nameof(RateItem)));
    }

    [Fact]
    public async Task Saving_without_changes_writes_no_audit_rows()
    {
        await AddRatePriceAsync(450.00m);
        var count = await _db.AuditEntries.CountAsync();

        await _db.SaveChangesAsync();

        Assert.Equal(count, await _db.AuditEntries.CountAsync());
    }

    [Fact]
    public async Task A_row_saved_with_its_parent_records_the_parents_real_key()
    {
        var item = new RateItem { Name = "Fabrication", Category = RateCategory.Fabrication, Unit = ChargeUnit.Hour };
        var price = new RatePrice { RateItem = item, Amount = 450.00m, EffectiveFrom = new DateOnly(2026, 1, 1) };

        _db.RatePrices.Add(price);   // the item is new too, so its id is temporary until the save
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(RatePrice) && a.PropertyName == nameof(RatePrice.RateItemId));

        Assert.Equal(item.Id.ToString(), entry.NewValue);
    }

    private async Task<(Customer Customer, Contact Contact)> AddCustomerAsync()
    {
        var customer = new Customer { Name = "RA Woodcraft" };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync();

        var contact = new Contact { CustomerId = customer.Id, FullName = "Test Contact" };
        _db.Contacts.Add(contact);
        await _db.SaveChangesAsync();

        return (customer, contact);
    }

    private async Task<Quote> AddQuoteAsync()
    {
        var (customer, contact) = await AddCustomerAsync();
        var quote = new Quote("TS-AUDIT-1", customer.Id, contact.Id, ManagingDirectorId, new DateOnly(2026, 10, 1));
        quote.StartNewVersion(ManagingDirectorId, 30m);
        _db.Quotes.Add(quote);
        await _db.SaveChangesAsync();
        return quote;
    }

    [Fact]
    public async Task Deactivating_a_customer_is_recorded()
    {
        var (customer, _) = await AddCustomerAsync();

        customer.IsActive = false;
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(Customer) && a.PropertyName == nameof(Customer.IsActive) && a.OldValue != null);
        Assert.Equal(customer.Id.ToString(), entry.EntityKey);
        Assert.Equal("True", entry.OldValue);
        Assert.Equal("False", entry.NewValue);
        Assert.Equal(ManagingDirectorId, entry.UserId);
    }

    [Fact]
    public async Task A_new_contact_is_recorded_with_its_customer()
    {
        var (customer, contact) = await AddCustomerAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(Contact) && a.PropertyName == nameof(Contact.CustomerId));
        Assert.Equal(contact.Id.ToString(), entry.EntityKey);
        Assert.Equal(customer.Id.ToString(), entry.NewValue);
    }

    [Fact]
    public async Task A_quotation_line_is_recorded_against_its_version()
    {
        var quote = await AddQuoteAsync();
        var version = quote.CurrentVersion!;

        var line = new QuotationLine("Kitchen tops in Glacier White", 12500.00m, "Kitchen");
        version.AddQuotationLine(line);
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(QuotationLine) && a.PropertyName == nameof(QuotationLine.QuoteVersionId));
        Assert.Equal(line.Id.ToString(), entry.EntityKey);
        Assert.Equal(version.Id.ToString(), entry.NewValue);
    }

    [Fact]
    public async Task Recording_a_pastel_invoice_is_recorded()
    {
        var quote = await AddQuoteAsync();

        var invoice = new InvoiceRecord(quote.Id, "IN114400", new DateOnly(2026, 10, 20), 14375.00m, ManagingDirectorId);
        _db.InvoiceRecords.Add(invoice);
        await _db.SaveChangesAsync();

        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(InvoiceRecord) && a.PropertyName == nameof(InvoiceRecord.InvoiceNumber));
        Assert.Equal(invoice.Id.ToString(), entry.EntityKey);
        Assert.Equal("IN114400", entry.NewValue);
    }

    [Fact]
    public async Task A_contacts_personal_details_are_not_stored_in_the_audit_trail()
    {
        var (_, contact) = await AddCustomerAsync();

        contact.Email = "new.address@example.co.za";
        await _db.SaveChangesAsync();

        // The change is recorded: which field, against which contact.
        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(Contact) && a.PropertyName == nameof(Contact.Email));
        Assert.Equal(contact.Id.ToString(), entry.EntityKey);
        Assert.Equal(AuditInterceptor.NotRecorded, entry.OldValue);
        Assert.Equal(AuditInterceptor.NotRecorded, entry.NewValue);

        // Neither the new email nor the name given when the contact was added is stored.
        Assert.False(await _db.AuditEntries.AnyAsync(a =>
            a.OldValue == "new.address@example.co.za" || a.NewValue == "new.address@example.co.za"));
        Assert.False(await _db.AuditEntries.AnyAsync(a => a.NewValue == "Test Contact"));
    }
}