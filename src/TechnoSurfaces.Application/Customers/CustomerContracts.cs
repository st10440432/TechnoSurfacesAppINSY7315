using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Application.Customers;

/// <summary>What the caller typed for a customer. Blank optional fields are stored as empty.</summary>
public sealed record CustomerInput(
    string Name,
    string? AccountCode = null,
    string? AddressLine1 = null,
    string? AddressLine2 = null,
    string? City = null,
    string? PostalCode = null,
    string? VatNumber = null);

/// <summary>What the caller typed for a contact at a customer.</summary>
public sealed record ContactInput(string FullName, string? Email = null, string? Phone = null, string? Position = null);

public sealed record ContactView(int Id, string FullName, string? Email, string? Phone, string? Position, bool IsActive)
{
    public static ContactView From(Contact c) => new(c.Id, c.FullName, c.Email, c.Phone, c.Position, c.IsActive);
}

/// <summary>A row on the customer list.</summary>
public sealed record CustomerSummary(int Id, string Name, string? AccountCode, string? City, bool IsActive, int ActiveContactCount);

/// <summary>
/// One page of the customer list, by name. <paramref name="Page"/> counts from 1 and is
/// always a page that exists, so asking past the end returns the last page.
/// </summary>
public sealed record CustomerPage(IReadOnlyList<CustomerSummary> Customers, int TotalCount, int Page, int PageSize)
{
    public int PageCount => Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
    public bool HasPrevious => Page > 1;
    public bool HasNext => Page < PageCount;

    /// <summary>The position of the first customer on this page, counting from 1; 0 when there are none.</summary>
    public int First => TotalCount == 0 ? 0 : (Page - 1) * PageSize + 1;

    public int Last => (Page - 1) * PageSize + Customers.Count;
}

/// <summary>A customer with all of its contacts, active and inactive.</summary>
public sealed record CustomerView(
    int Id,
    string Name,
    string? AccountCode,
    string? AddressLine1,
    string? AddressLine2,
    string? City,
    string? PostalCode,
    string? VatNumber,
    bool IsActive,
    IReadOnlyList<ContactView> Contacts)
{
    public static CustomerView From(Customer c) => new(
        c.Id, c.Name, c.AccountCode, c.AddressLine1, c.AddressLine2, c.City, c.PostalCode, c.VatNumber, c.IsActive,
        c.Contacts.OrderBy(k => k.FullName).Select(ContactView.From).ToList());
}

/// <summary>A contact that a new quote can be addressed to.</summary>
public sealed record ContactChoice(int Id, string FullName, string? Position);

/// <summary>A customer a new quote can be billed to, with the contacts it can be addressed to (US-15).</summary>
public sealed record CustomerChoice(int Id, string Name, string? AccountCode, IReadOnlyList<ContactChoice> Contacts);

public enum CustomerOutcome
{
    Ok,
    NotFound,

    /// <summary>The input failed validation; <see cref="CustomerResult.Errors"/> names the fields.</summary>
    Invalid,

    /// <summary>Another customer already has this Pastel account code.</summary>
    AccountCodeTaken
}

public sealed record CustomerResult(
    CustomerOutcome Outcome,
    CustomerView? Customer = null,
    ContactView? Contact = null,
    IReadOnlyDictionary<string, string[]>? Errors = null,
    string? Problem = null);

/// <summary>
/// Loads and saves customers. Declared here and implemented in the Infrastructure
/// layer, so the Application layer does not depend on EF Core.
/// </summary>
public interface ICustomerRepository
{
    /// <summary>Customers matching the search on name or account code, ordered by name.</summary>
    Task<IReadOnlyList<CustomerSummary>> SearchAsync(string? search, bool includeInactive, CancellationToken ct = default);

    /// <summary>One page of <see cref="SearchAsync"/>, with the number of customers that match.</summary>
    Task<CustomerPage> SearchPageAsync(string? search, bool includeInactive, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Active customers that have at least one active contact, with those contacts.</summary>
    Task<IReadOnlyList<CustomerChoice>> ChoicesAsync(CancellationToken ct = default);

    /// <summary>A customer with its contacts, tracked for changes.</summary>
    Task<Customer?> GetAsync(int customerId, CancellationToken ct = default);

    Task<bool> AccountCodeTakenAsync(string accountCode, int? exceptCustomerId, CancellationToken ct = default);

    /// <summary>The account code and name of every customer, active or not, for the Pastel import.</summary>
    Task<IReadOnlyList<ExistingCustomerKey>> ExistingKeysAsync(CancellationToken ct = default);

    void Add(Customer customer);

    Task SaveChangesAsync(CancellationToken ct = default);
}
