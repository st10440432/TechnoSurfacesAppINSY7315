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

    [Fact]
    public async Task The_customer_list_comes_a_page_at_a_time_in_name_order()
    {
        for (var i = 1; i <= CustomerService.ListPageSize + 3; i++)
            await CreateAsync($"Customer {i:00}", $"PAG{i:00}");

        var first = await WithServiceAsync(s => s.ListPageAsync(null, false, 1));
        var second = await WithServiceAsync(s => s.ListPageAsync(null, false, 2));

        Assert.Equal(CustomerService.ListPageSize + 3, first.TotalCount);
        Assert.Equal(2, first.PageCount);
        Assert.Equal(CustomerService.ListPageSize, first.Customers.Count);
        Assert.Equal("Customer 01", first.Customers[0].Name);
        Assert.Equal(["Customer 26", "Customer 27", "Customer 28"], second.Customers.Select(c => c.Name));
        Assert.Equal((26, 28), (second.First, second.Last));
    }

    [Fact]
    public async Task A_page_past_the_end_shows_the_last_page_and_the_search_is_counted()
    {
        for (var i = 1; i <= 30; i++)
            await CreateAsync(i % 2 == 0 ? $"Even {i:00}" : $"Odd {i:00}");

        var past = await WithServiceAsync(s => s.ListPageAsync(null, false, 99));
        var evens = await WithServiceAsync(s => s.ListPageAsync("Even", false, 1));

        Assert.Equal(2, past.Page);
        Assert.Equal(5, past.Customers.Count);
        Assert.Equal((15, 1), (evens.TotalCount, evens.PageCount));
    }
}
