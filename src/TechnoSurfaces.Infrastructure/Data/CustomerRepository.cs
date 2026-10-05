using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Customers;
using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// EF Core implementation of <see cref="ICustomerRepository"/>. The two list queries
/// are no-tracking projections; only the single customer being changed is tracked.
/// </summary>
public sealed class CustomerRepository : ICustomerRepository
{
    private readonly TechnoSurfacesDbContext _db;

    public CustomerRepository(TechnoSurfacesDbContext db) => _db = db;

    public async Task<IReadOnlyList<CustomerSummary>> SearchAsync(string? search, bool includeInactive, CancellationToken ct = default)
    {
        var query = _db.Customers.AsNoTracking();

        if (!includeInactive)
            query = query.Where(c => c.IsActive);

        // Searched on the company and its Pastel account code only. A contact's name
        // is personal information and is kept out of the search, so it never travels
        // in a URL or a request log (NFR-09).
        if (search is not null)
            query = query.Where(c => c.Name.Contains(search) || (c.AccountCode != null && c.AccountCode.Contains(search)));

        return await query
            .OrderBy(c => c.Name)
            .Select(c => new CustomerSummary(
                c.Id, c.Name, c.AccountCode, c.City, c.IsActive,
                c.Contacts.Count(k => k.IsActive)))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<CustomerChoice>> ChoicesAsync(CancellationToken ct = default)
    {
        var rows = await _db.Customers
            .AsNoTracking()
            .Where(c => c.IsActive && c.Contacts.Any(k => k.IsActive))
            .OrderBy(c => c.Name)
            .Select(c => new
            {
                c.Id,
                c.Name,
                c.AccountCode,
                Contacts = c.Contacts
                    .Where(k => k.IsActive)
                    .OrderBy(k => k.FullName)
                    .Select(k => new ContactChoice(k.Id, k.FullName, k.Position))
                    .ToList()
            })
            .ToListAsync(ct);

        return rows.Select(c => new CustomerChoice(c.Id, c.Name, c.AccountCode, c.Contacts)).ToList();
    }

    public Task<Customer?> GetAsync(int customerId, CancellationToken ct = default) =>
        _db.Customers
            .Include(c => c.Contacts)
            .FirstOrDefaultAsync(c => c.Id == customerId, ct);

    public Task<bool> AccountCodeTakenAsync(string accountCode, int? exceptCustomerId, CancellationToken ct = default) =>
        _db.Customers.AnyAsync(c => c.AccountCode == accountCode && c.Id != exceptCustomerId, ct);

    public void Add(Customer customer) => _db.Customers.Add(customer);

    public Task SaveChangesAsync(CancellationToken ct = default) => _db.SaveChangesAsync(ct);
}
