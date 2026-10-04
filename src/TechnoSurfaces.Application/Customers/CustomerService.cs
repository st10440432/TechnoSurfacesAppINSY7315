using System.ComponentModel.DataAnnotations;
using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Application.Customers;

public interface ICustomerService
{
    Task<IReadOnlyList<CustomerSummary>> ListAsync(string? search = null, bool includeInactive = false, CancellationToken ct = default);

    Task<CustomerResult> GetAsync(int customerId, CancellationToken ct = default);

    /// <summary>The customer and contact choice on the new-quote form: active ones only.</summary>
    Task<IReadOnlyList<CustomerChoice>> ChoicesForNewQuoteAsync(CancellationToken ct = default);

    Task<CustomerResult> CreateAsync(CustomerInput input, CancellationToken ct = default);

    Task<CustomerResult> UpdateAsync(int customerId, CustomerInput input, CancellationToken ct = default);

    /// <summary>Deactivates or reactivates a customer. There is no delete.</summary>
    Task<CustomerResult> SetActiveAsync(int customerId, bool isActive, CancellationToken ct = default);

    Task<CustomerResult> AddContactAsync(int customerId, ContactInput input, CancellationToken ct = default);

    Task<CustomerResult> UpdateContactAsync(int customerId, int contactId, ContactInput input, CancellationToken ct = default);

    /// <summary>Deactivates or reactivates a contact. There is no delete.</summary>
    Task<CustomerResult> SetContactActiveAsync(int customerId, int contactId, bool isActive, CancellationToken ct = default);
}

/// <summary>
/// Customers and their contacts (US-15). A quotation is billed to a customer and
/// addressed to a contact, and some customers have four or five people sending
/// requests, so a customer holds many contacts.
///
/// Both roles maintain customers, so this service needs no policy beyond being
/// signed in. Nothing is ever deleted: old quotes must still show who they were for,
/// and the foreign keys are NoAction. A record is deactivated instead, which takes it
/// off the new-quote choice.
///
/// Input is validated here as well as at the API, so the rules hold for any caller.
/// The lengths match the column sizes in CustomerConfiguration.
/// </summary>
public sealed class CustomerService : ICustomerService
{
    private static readonly EmailAddressAttribute EmailRule = new();

    private readonly ICustomerRepository _customers;

    public CustomerService(ICustomerRepository customers) => _customers = customers;

    public Task<IReadOnlyList<CustomerSummary>> ListAsync(string? search = null, bool includeInactive = false, CancellationToken ct = default) =>
        _customers.SearchAsync(Clean(search), includeInactive, ct);

    public async Task<CustomerResult> GetAsync(int customerId, CancellationToken ct = default)
    {
        var customer = await _customers.GetAsync(customerId, ct);
        return customer is null
            ? new(CustomerOutcome.NotFound)
            : new(CustomerOutcome.Ok, CustomerView.From(customer));
    }

    public Task<IReadOnlyList<CustomerChoice>> ChoicesForNewQuoteAsync(CancellationToken ct = default) =>
        _customers.ChoicesAsync(ct);

    public async Task<CustomerResult> CreateAsync(CustomerInput input, CancellationToken ct = default)
    {
        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(CustomerOutcome.Invalid, Errors: errors);
        if (clean.AccountCode is { } code && await _customers.AccountCodeTakenAsync(code, null, ct))
            return AccountCodeTaken(code);

        var customer = new Customer();
        Apply(customer, clean);
        _customers.Add(customer);
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer));
    }

    public async Task<CustomerResult> UpdateAsync(int customerId, CustomerInput input, CancellationToken ct = default)
    {
        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null)
            return new(CustomerOutcome.NotFound);

        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(CustomerOutcome.Invalid, Errors: errors);
        if (clean.AccountCode is { } code && await _customers.AccountCodeTakenAsync(code, customerId, ct))
            return AccountCodeTaken(code);

        Apply(customer, clean);
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer));
    }

    public async Task<CustomerResult> SetActiveAsync(int customerId, bool isActive, CancellationToken ct = default)
    {
        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null)
            return new(CustomerOutcome.NotFound);

        customer.IsActive = isActive;
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer));
    }

    public async Task<CustomerResult> AddContactAsync(int customerId, ContactInput input, CancellationToken ct = default)
    {
        var customer = await _customers.GetAsync(customerId, ct);
        if (customer is null)
            return new(CustomerOutcome.NotFound);

        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(CustomerOutcome.Invalid, Errors: errors);

        var contact = new Contact { CustomerId = customerId };
        Apply(contact, clean);
        customer.Contacts.Add(contact);
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer), ContactView.From(contact));
    }

    public async Task<CustomerResult> UpdateContactAsync(int customerId, int contactId, ContactInput input, CancellationToken ct = default)
    {
        var (customer, contact) = await FindContactAsync(customerId, contactId, ct);
        if (contact is null)
            return new(CustomerOutcome.NotFound);

        var clean = Normalise(input);
        if (Validate(clean) is { } errors)
            return new(CustomerOutcome.Invalid, Errors: errors);

        Apply(contact, clean);
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer!), ContactView.From(contact));
    }

    public async Task<CustomerResult> SetContactActiveAsync(int customerId, int contactId, bool isActive, CancellationToken ct = default)
    {
        var (customer, contact) = await FindContactAsync(customerId, contactId, ct);
        if (contact is null)
            return new(CustomerOutcome.NotFound);

        contact.IsActive = isActive;
        await _customers.SaveChangesAsync(ct);
        return new(CustomerOutcome.Ok, CustomerView.From(customer!), ContactView.From(contact));
    }

    /// <summary>A contact is found only through the customer it belongs to.</summary>
    private async Task<(Customer? Customer, Contact? Contact)> FindContactAsync(int customerId, int contactId, CancellationToken ct)
    {
        var customer = await _customers.GetAsync(customerId, ct);
        return (customer, customer?.Contacts.FirstOrDefault(k => k.Id == contactId));
    }

    private static CustomerResult AccountCodeTaken(string code) =>
        new(CustomerOutcome.AccountCodeTaken,
            Problem: $"Another customer already has the Pastel account code {code}.");

    private static void Apply(Customer customer, CustomerInput input)
    {
        customer.Name = input.Name;
        customer.AccountCode = input.AccountCode;
        customer.AddressLine1 = input.AddressLine1;
        customer.AddressLine2 = input.AddressLine2;
        customer.City = input.City;
        customer.PostalCode = input.PostalCode;
        customer.VatNumber = input.VatNumber;
    }

    private static void Apply(Contact contact, ContactInput input)
    {
        contact.FullName = input.FullName;
        contact.Email = input.Email;
        contact.Phone = input.Phone;
        contact.Position = input.Position;
    }

    // ---- Input rules ----

    private static CustomerInput Normalise(CustomerInput i) => new(
        Clean(i.Name) ?? "",
        Clean(i.AccountCode)?.ToUpperInvariant(),
        Clean(i.AddressLine1),
        Clean(i.AddressLine2),
        Clean(i.City),
        Clean(i.PostalCode),
        Clean(i.VatNumber));

    private static ContactInput Normalise(ContactInput i) => new(
        Clean(i.FullName) ?? "",
        Clean(i.Email),
        Clean(i.Phone),
        Clean(i.Position));

    private static Dictionary<string, string[]>? Validate(CustomerInput i)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, nameof(i.Name), i.Name, "A customer needs a name.");
        MaxLength(errors, nameof(i.Name), i.Name, 200);
        MaxLength(errors, nameof(i.AccountCode), i.AccountCode, 40);
        MaxLength(errors, nameof(i.AddressLine1), i.AddressLine1, 200);
        MaxLength(errors, nameof(i.AddressLine2), i.AddressLine2, 200);
        MaxLength(errors, nameof(i.City), i.City, 100);
        MaxLength(errors, nameof(i.PostalCode), i.PostalCode, 20);
        MaxLength(errors, nameof(i.VatNumber), i.VatNumber, 40);
        return errors.Count == 0 ? null : errors;
    }

    private static Dictionary<string, string[]>? Validate(ContactInput i)
    {
        var errors = new Dictionary<string, string[]>();
        Required(errors, nameof(i.FullName), i.FullName, "A contact needs a name.");
        MaxLength(errors, nameof(i.FullName), i.FullName, 200);
        MaxLength(errors, nameof(i.Email), i.Email, 200);
        MaxLength(errors, nameof(i.Phone), i.Phone, 40);
        MaxLength(errors, nameof(i.Position), i.Position, 100);
        if (i.Email is not null && !EmailRule.IsValid(i.Email) && !errors.ContainsKey(nameof(i.Email)))
            errors[nameof(i.Email)] = new[] { "This is not a valid email address." };
        return errors.Count == 0 ? null : errors;
    }

    private static void Required(Dictionary<string, string[]> errors, string field, string value, string message)
    {
        if (string.IsNullOrEmpty(value))
            errors[field] = new[] { message };
    }

    private static void MaxLength(Dictionary<string, string[]> errors, string field, string? value, int max)
    {
        if (value is not null && value.Length > max && !errors.ContainsKey(field))
            errors[field] = new[] { $"No more than {max} characters." };
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
