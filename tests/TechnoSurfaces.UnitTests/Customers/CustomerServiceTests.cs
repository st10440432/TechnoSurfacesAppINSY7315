using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Infrastructure.Data;

namespace TechnoSurfaces.UnitTests.Customers;

/// <summary>
/// Customers and contacts (US-15) on SQLite: create, edit, deactivate and reactivate,
/// validation, and the choice offered on a new quote.
/// </summary>
public sealed class CustomerServiceTests : IAsyncLifetime
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

    private async Task<T> WithServiceAsync<T>(Func<CustomerService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await act(new CustomerService(new CustomerRepository(db)));
    }

    private Task<CustomerResult> CreateAsync(string name, string? accountCode = null) =>
        WithServiceAsync(s => s.CreateAsync(new CustomerInput(name, accountCode, City: "Cape Town")));

    [Fact]
    public async Task A_customer_is_created_and_read_back()
    {
        var created = await CreateAsync("  RA Woodcraft  ", "raw001");

        Assert.Equal(CustomerOutcome.Ok, created.Outcome);

        var read = await WithServiceAsync(s => s.GetAsync(created.Customer!.Id));
        Assert.Equal("RA Woodcraft", read.Customer!.Name);
        Assert.Equal("RAW001", read.Customer.AccountCode);
        Assert.Equal("Cape Town", read.Customer.City);
        Assert.True(read.Customer.IsActive);
    }

    [Fact]
    public async Task A_customer_without_a_name_is_refused()
    {
        var result = await CreateAsync("   ");

        Assert.Equal(CustomerOutcome.Invalid, result.Outcome);
        Assert.Contains(nameof(CustomerInput.Name), result.Errors!.Keys);
        Assert.Empty(await WithServiceAsync(s => s.ListAsync(includeInactive: true)));
    }

    [Fact]
    public async Task A_field_longer_than_its_column_is_refused()
    {
        var result = await WithServiceAsync(s => s.CreateAsync(new CustomerInput("Customer", VatNumber: new string('9', 41))));

        Assert.Equal(CustomerOutcome.Invalid, result.Outcome);
        Assert.Contains(nameof(CustomerInput.VatNumber), result.Errors!.Keys);
    }

    [Fact]
    public async Task A_pastel_account_code_belongs_to_one_customer()
    {
        await CreateAsync("RA Woodcraft", "RAW001");
        var other = await CreateAsync("Another customer");

        Assert.Equal(CustomerOutcome.AccountCodeTaken, (await CreateAsync("Second", "raw001")).Outcome);
        Assert.Equal(CustomerOutcome.AccountCodeTaken, (await WithServiceAsync(s =>
            s.UpdateAsync(other.Customer!.Id, new CustomerInput("Another customer", "RAW001")))).Outcome);
    }

    [Fact]
    public async Task A_customer_keeps_its_own_account_code_when_edited()
    {
        var created = await CreateAsync("RA Woodcraft", "RAW001");

        var updated = await WithServiceAsync(s =>
            s.UpdateAsync(created.Customer!.Id, new CustomerInput("RA Woodcraft (Pty) Ltd", "RAW001", VatNumber: "4690293412")));

        Assert.Equal(CustomerOutcome.Ok, updated.Outcome);
        Assert.Equal("RA Woodcraft (Pty) Ltd", updated.Customer!.Name);
        Assert.Equal("4690293412", updated.Customer.VatNumber);
    }

    [Fact]
    public async Task A_customer_has_many_contacts()
    {
        var customer = await CreateAsync("RA Woodcraft");
        var id = customer.Customer!.Id;

        await WithServiceAsync(s => s.AddContactAsync(id, new ContactInput("First estimator", "first@example.com")));
        var second = await WithServiceAsync(s => s.AddContactAsync(id, new ContactInput("Second estimator", Position: "Buyer")));

        Assert.Equal(CustomerOutcome.Ok, second.Outcome);
        Assert.Equal(2, second.Customer!.Contacts.Count);
        Assert.Equal("Buyer", second.Contact!.Position);
    }

    [Fact]
    public async Task A_contact_with_an_invalid_email_is_refused()
    {
        var customer = await CreateAsync("RA Woodcraft");

        var result = await WithServiceAsync(s =>
            s.AddContactAsync(customer.Customer!.Id, new ContactInput("Contact", "not an email")));

        Assert.Equal(CustomerOutcome.Invalid, result.Outcome);
        Assert.Contains(nameof(ContactInput.Email), result.Errors!.Keys);
    }

    [Fact]
    public async Task A_contact_is_only_reached_through_its_own_customer()
    {
        var first = await CreateAsync("First");
        var second = await CreateAsync("Second");
        var contact = await WithServiceAsync(s => s.AddContactAsync(first.Customer!.Id, new ContactInput("Contact")));

        var result = await WithServiceAsync(s =>
            s.UpdateContactAsync(second.Customer!.Id, contact.Contact!.Id, new ContactInput("Renamed")));

        Assert.Equal(CustomerOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task A_deactivated_customer_leaves_the_new_quote_choice_but_stays_readable()
    {
        var customer = await CreateAsync("RA Woodcraft");
        var id = customer.Customer!.Id;
        await WithServiceAsync(s => s.AddContactAsync(id, new ContactInput("Contact")));

        Assert.Single(await WithServiceAsync(s => s.ChoicesForNewQuoteAsync()));

        await WithServiceAsync(s => s.SetActiveAsync(id, false));

        Assert.Empty(await WithServiceAsync(s => s.ChoicesForNewQuoteAsync()));
        Assert.Empty(await WithServiceAsync(s => s.ListAsync()));
        Assert.Single(await WithServiceAsync(s => s.ListAsync(includeInactive: true)));
        Assert.False((await WithServiceAsync(s => s.GetAsync(id))).Customer!.IsActive);

        await WithServiceAsync(s => s.SetActiveAsync(id, true));
        Assert.Single(await WithServiceAsync(s => s.ChoicesForNewQuoteAsync()));
    }

    [Fact]
    public async Task A_deactivated_contact_is_not_offered_on_a_new_quote()
    {
        var customer = await CreateAsync("RA Woodcraft");
        var id = customer.Customer!.Id;
        var leaving = await WithServiceAsync(s => s.AddContactAsync(id, new ContactInput("Leaving")));
        await WithServiceAsync(s => s.AddContactAsync(id, new ContactInput("Staying")));

        await WithServiceAsync(s => s.SetContactActiveAsync(id, leaving.Contact!.Id, false));

        var choice = Assert.Single(await WithServiceAsync(s => s.ChoicesForNewQuoteAsync()));
        Assert.Equal("Staying", Assert.Single(choice.Contacts).FullName);
    }

    [Fact]
    public async Task A_customer_with_no_active_contact_cannot_be_chosen_for_a_quote()
    {
        await CreateAsync("No contacts yet");

        Assert.Empty(await WithServiceAsync(s => s.ChoicesForNewQuoteAsync()));
    }

    [Fact]
    public async Task The_list_searches_name_and_account_code()
    {
        await CreateAsync("RA Woodcraft", "RAW001");
        await CreateAsync("Bootleggers", "BOO001");

        Assert.Equal("RA Woodcraft", Assert.Single(await WithServiceAsync(s => s.ListAsync("Wood"))).Name);
        Assert.Equal("Bootleggers", Assert.Single(await WithServiceAsync(s => s.ListAsync("BOO0"))).Name);
    }

    [Fact]
    public async Task An_unknown_customer_is_not_found()
    {
        Assert.Equal(CustomerOutcome.NotFound, (await WithServiceAsync(s => s.GetAsync(999))).Outcome);
        Assert.Equal(CustomerOutcome.NotFound, (await WithServiceAsync(s =>
            s.AddContactAsync(999, new ContactInput("Contact")))).Outcome);
    }
}
