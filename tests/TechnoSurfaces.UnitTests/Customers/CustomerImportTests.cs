using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Infrastructure.Data;

namespace TechnoSurfaces.UnitTests.Customers;

/// <summary>
/// The import from the Sage Pastel Customer Listing, on SQLite: reading the report,
/// turning each record into a customer and contact by the rules the team chose,
/// and never changing a customer that is already in the app. The customers
/// here are made up; they only follow the layout of the client's export.
/// </summary>
public sealed class CustomerImportTests : IAsyncLifetime
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

    private async Task<T> WithImportAsync<T>(Func<CustomerImportService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await act(new CustomerImportService(new CustomerRepository(db)));
    }

    private async Task<List<Customer>> CustomersAsync()
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await db.Customers.Include(c => c.Contacts).OrderBy(c => c.AccountCode).ToListAsync();
    }

    private async Task AddExistingAsync(string name, string? accountCode)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        db.Customers.Add(new Customer { Name = name, AccountCode = accountCode, City = "Typed in the app" });
        await db.SaveChangesAsync();
    }

    // ---- A listing in the client's layout ----

    private const string Heading = "\"Prepared by:  Example Trading (Pty) Ltd\"\r\n\"Customer Listing\"\r\n \r\n \r\n";

    private static string Record(
        string code, string name, string[]? delivery = null, string[]? postal = null,
        string contact = "", string telephone = "", string mobile = "", string email = "",
        string taxReference = "", string blocked = "No")
    {
        delivery ??= [];
        postal ??= [];
        string Line(int i, string[] lines) => i < lines.Length ? lines[i] : "";

        var labels = new (string Label, string Value, string Label2, string Value2)[]
        {
            ("Mobile Phone:", mobile, "Statement:", "P OM"),
            ("Email:", email, "Blocked:", blocked),
            ("Type:", "O/I", "Price List:", "Price List 1"),
            ("Category:", "0", "Sales Code:", ""),
            ("Normal Terms:", "0", "Tax Mode:", "No tax type default"),
            ("Early Terms:", "0", "Tax Type:", "0"),
            ("", "", "Tax Reference:", taxReference),
            ("", "", "Ship / Deliver:", ""),
            ("No. of Notes:", "0", "Freight:", ""),
            ("", "", "Cash Sale Acc:", "No"),
            ("", "", "Accepts Email:", "No")
        };

        var sb = new StringBuilder();
        sb.Append($"\"Acc\",\"{code}\",\"Telephone:\",\"{telephone}\",\"Disc%:\",\"0.00%\"\r\n");
        sb.Append($"\"Name\",\"{name.Replace("\"", "\"\"")}\",\"Fax:\",\"\",\"Cr Limit:\",\"0\"\r\n");
        sb.Append($"\"Delivery Address\",\"Postal Address\",\"Contact:\",\"{contact}\",\"Interest:\",\"None\"\r\n");
        for (var i = 0; i < labels.Length; i++)
        {
            var (l1, v1, l2, v2) = labels[i];
            // Pastel leaves the third column bare (,,) where a row has no label there.
            var left = l1.Length == 0 ? $"\"{Line(i, delivery)}\",\"{Line(i, postal)}\",," : $"\"{Line(i, delivery)}\",\"{Line(i, postal)}\",\"{l1}\",";
            sb.Append($"{left}\"{v1}\",\"{l2}\",\"{v2}\"\r\n");
        }
        sb.Append(" \r\n \r\n");
        return sb.ToString();
    }

    private static PastelListing Listing(params string[] records) =>
        PastelCustomerListing.Parse(Heading + string.Concat(records));

    // ---- Reading the report ----

    [Fact]
    public void Every_field_of_a_record_is_read_and_the_heading_is_ignored()
    {
        var listing = Listing(Record("T-EXA1", "Example Kitchens (Pty) Ltd",
            delivery: ["1 Harbour Road", "Paarden Eiland"],
            postal: ["PO Box 12", "Paarden Eiland", "Cape Town 7405"],
            contact: "Thandi", telephone: "021 555 0100", mobile: "082 555 0101",
            email: "thandi@example.co.za", taxReference: "4123456789"));

        Assert.Empty(listing.Problems);
        var r = Assert.Single(listing.Customers);
        Assert.Equal("T-EXA1", r.AccountCode);
        Assert.Equal("Example Kitchens (Pty) Ltd", r.Name);
        Assert.Equal(["1 Harbour Road", "Paarden Eiland"], r.DeliveryAddress);
        Assert.Equal(["PO Box 12", "Paarden Eiland", "Cape Town 7405"], r.PostalAddress);
        Assert.Equal("Thandi", r.Contact);
        Assert.Equal("021 555 0100", r.Telephone);
        Assert.Equal("082 555 0101", r.MobilePhone);
        Assert.Equal("thandi@example.co.za", r.Email);
        Assert.Equal("4123456789", r.TaxReference);
        Assert.False(r.Blocked);
    }

    [Fact]
    public void A_comma_or_a_quote_inside_a_name_is_kept()
    {
        var r = Assert.Single(Listing(Record("T-QUO1", "Smith, Jones & \"Partners\"")).Customers);

        Assert.Equal("Smith, Jones & \"Partners\"", r.Name);
    }

    [Fact]
    public void A_file_with_no_customers_says_so()
    {
        var listing = PastelCustomerListing.Parse("just some text\r\nthat is not a listing\r\n");

        Assert.Empty(listing.Customers);
        Assert.Single(listing.Problems);
    }

    [Fact]
    public async Task A_windows_encoded_file_keeps_its_accented_letters()
    {
        var bytes = Encoding.Latin1.GetBytes(Heading + Record("T-CAF1", "Café Example"));

        var listing = await PastelCustomerListing.ReadAsync(new MemoryStream(bytes));

        Assert.Equal("Café Example", Assert.Single(listing.Customers).Name);
    }

    // ---- The address ----

    [Theory]
    [InlineData(new[] { "Postnet Suite 9", "Private Bag X1", "Chempet 7442" }, "Postnet Suite 9", "Private Bag X1", "Chempet", "7442")]
    [InlineData(new[] { "P.O. Box 52", "Helderberg", "Somerset West" }, "P.O. Box 52", "Helderberg", "Somerset West", null)]
    [InlineData(new[] { "12 Main Road", "Claremont" }, "12 Main Road", null, "Claremont", null)]
    [InlineData(new[] { "Unit 4", "Example Park", "Montague Gardens", "7441" }, "Unit 4", "Example Park", "Montague Gardens", "7441")]
    [InlineData(new[] { "Unit 4", "Example Park", "Old Road", "Durbanville", "Cape Town, 7550" }, "Unit 4", "Example Park, Old Road, Durbanville", "Cape Town", "7550")]
    [InlineData(new[] { "PO Box 7" }, "PO Box 7", null, null, null)]
    public void The_free_address_lines_are_split_without_losing_any(string[] lines, string? line1, string? line2, string? city, string? code)
    {
        var a = CustomerImportService.SplitAddress(lines);

        Assert.Equal((line1, line2, city, code), a);
    }

    // ---- The plan ----

    [Fact]
    public async Task The_postal_address_is_used_and_the_delivery_address_when_there_is_no_postal_one()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(
            Record("T-POS1", "Has Both", delivery: ["1 Site Road", "Epping"], postal: ["PO Box 1", "Epping 7475"]),
            Record("T-DEL1", "Delivery Only", delivery: ["2 Site Road", "Epping 7460"]))));

        var both = plan.ToAdd.Single(r => r.Customer.AccountCode == "T-POS1").Customer;
        Assert.Equal(("PO Box 1", "Epping", "7475"), (both.AddressLine1, both.City, both.PostalCode));
        var deliveryOnly = plan.ToAdd.Single(r => r.Customer.AccountCode == "T-DEL1").Customer;
        Assert.Equal(("2 Site Road", "Epping", "7460"), (deliveryOnly.AddressLine1, deliveryOnly.City, deliveryOnly.PostalCode));
    }

    [Fact]
    public async Task The_contact_keeps_both_phone_numbers_and_the_tax_reference_becomes_the_vat_number()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(
            Record("T-PHO1", "Phones", contact: "Sipho", telephone: "021 555 0200", mobile: "083 555 0201",
                email: "sipho@example.co.za", taxReference: "4987654321"))));

        var row = Assert.Single(plan.ToAdd);
        Assert.Equal("4987654321", row.Customer.VatNumber);
        Assert.Equal(new ContactInput("Sipho", "sipho@example.co.za", "021 555 0200 / 083 555 0201"), row.Contact);
        Assert.Empty(row.Notes);
    }

    [Fact]
    public async Task A_customer_with_no_contact_name_gets_no_contact_rather_than_a_made_up_one()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(
            Record("T-NOC1", "No Contact", email: "accounts@example.co.za"))));

        var row = Assert.Single(plan.ToAdd);
        Assert.Null(row.Contact);
        Assert.Contains(row.Notes, n => n.Contains("No contact person"));
    }

    [Fact]
    public async Task An_email_that_is_not_one_valid_address_is_left_off_and_noted()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(
            Record("T-EMA1", "Two Emails", contact: "Anna", email: "anna@example.co.za; ben@example.co.za"))));

        var row = Assert.Single(plan.ToAdd);
        Assert.Equal("Anna", row.Contact!.FullName);
        Assert.Null(row.Contact.Email);
        Assert.Contains(row.Notes, n => n.Contains("anna@example.co.za; ben@example.co.za"));
    }

    [Fact]
    public async Task A_customer_blocked_in_pastel_comes_in_inactive()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(Record("T-BLO1", "Blocked", contact: "Ivan", blocked: "Yes"))));

        Assert.False(Assert.Single(plan.ToAdd).IsActive);
        Assert.Equal(1, plan.InactiveCount);
    }

    [Fact]
    public async Task A_customer_with_no_name_is_left_out_with_the_reason()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(Record("T-NON1", ""))));

        Assert.Empty(plan.ToAdd);
        var skip = Assert.Single(plan.Skipped);
        Assert.Equal("T-NON1", skip.AccountCode);
        Assert.Equal("There is no name in Pastel.", skip.Reason);
    }

    [Fact]
    public async Task An_account_code_that_appears_twice_in_the_file_is_imported_once()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(Record("T-TWI1", "First"), Record("t-twi1", "Second"))));

        Assert.Equal("First", Assert.Single(plan.ToAdd).Customer.Name);
        Assert.Single(plan.Skipped);
    }

    [Fact]
    public async Task A_customer_already_in_the_app_is_left_as_it_is()
    {
        await AddExistingAsync("Existing By Code", "T-EXI1");
        await AddExistingAsync("Typed In Without A Code", null);

        var plan = await WithImportAsync(s => s.PlanAsync(Listing(
            Record("t-exi1", "Existing By Code (new name in Pastel)"),
            Record("T-TYP1", "typed in without a code"),
            Record("T-NEW1", "New Customer"))));

        Assert.Equal("T-NEW1", Assert.Single(plan.ToAdd).Customer.AccountCode);
        Assert.Equal(["T-EXI1", "T-TYP1"], plan.Skipped.Select(s => s.AccountCode).Order());
        var existing = (await CustomersAsync()).Single(c => c.AccountCode == "T-EXI1");
        Assert.Equal("Existing By Code", existing.Name);
    }

    // ---- Importing ----

    [Fact]
    public async Task Importing_saves_the_customers_and_contacts_as_planned()
    {
        var listing = Listing(
            Record("T-IMP1", "Imported One", postal: ["PO Box 3", "Bellville 7535"], contact: "Lindiwe",
                email: "lindiwe@example.co.za", telephone: "021 555 0300", taxReference: "4111111111"),
            Record("T-IMP2", "Imported Two", blocked: "Yes"));

        var result = await WithImportAsync(async s => await s.ImportAsync(await s.PlanAsync(listing)));

        Assert.Equal((2, 1), (result.CustomersAdded, result.ContactsAdded));
        var saved = await CustomersAsync();
        var one = saved.Single(c => c.AccountCode == "T-IMP1");
        Assert.Equal(("Imported One", "PO Box 3", "Bellville", "7535", "4111111111", true),
            (one.Name, one.AddressLine1, one.City, one.PostalCode, one.VatNumber, one.IsActive));
        var contact = Assert.Single(one.Contacts);
        Assert.Equal(("Lindiwe", "lindiwe@example.co.za", "021 555 0300"), (contact.FullName, contact.Email, contact.Phone));
        var two = saved.Single(c => c.AccountCode == "T-IMP2");
        Assert.False(two.IsActive);
        Assert.Empty(two.Contacts);
    }

    [Fact]
    public async Task A_listing_larger_than_one_batch_is_saved_in_full()
    {
        var count = CustomerImportService.BatchSize * 2 + 7;
        var listing = Listing(Enumerable.Range(1, count).Select(i => Record($"T-BAT{i:000}", $"Batch {i}", contact: "Person")).ToArray());

        var result = await WithImportAsync(async s => await s.ImportAsync(await s.PlanAsync(listing)));

        Assert.Equal(count, result.CustomersAdded);
        Assert.Equal(count, (await CustomersAsync()).Count);
    }

    [Fact]
    public async Task Running_the_same_import_again_adds_nothing()
    {
        var listing = Listing(Record("T-AGA1", "Again", contact: "Ruth"));
        await WithImportAsync(async s => await s.ImportAsync(await s.PlanAsync(listing)));

        var second = await WithImportAsync(async s => await s.ImportAsync(await s.PlanAsync(listing)));

        Assert.Equal(0, second.CustomersAdded);
        Assert.Single(await CustomersAsync());
    }

    [Fact]
    public async Task A_customer_added_between_the_preview_and_the_import_is_not_added_twice()
    {
        var plan = await WithImportAsync(s => s.PlanAsync(Listing(Record("T-RAC1", "Race"))));
        await AddExistingAsync("Race", "T-RAC1");

        var result = await WithImportAsync(s => s.ImportAsync(plan));

        Assert.Equal(0, result.CustomersAdded);
        Assert.Contains(result.Skipped, s => s.AccountCode == "T-RAC1");
        Assert.Single(await CustomersAsync());
    }
}
