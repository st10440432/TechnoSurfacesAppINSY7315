using System.Text.RegularExpressions;
using TechnoSurfaces.Domain.People;

namespace TechnoSurfaces.Application.Customers;

/// <summary>A customer from the listing that will be added, with the contact made from it, if any.</summary>
public sealed record CustomerImportRow(
    CustomerInput Customer,
    ContactInput? Contact,
    bool IsActive,
    IReadOnlyList<string> Notes);

/// <summary>A customer from the listing that will not be added, and why.</summary>
public sealed record CustomerImportSkip(string AccountCode, string Name, string Reason);

/// <summary>What an import would do, shown to the Managing Director before anything is saved.</summary>
public sealed record CustomerImportPlan(
    IReadOnlyList<CustomerImportRow> ToAdd,
    IReadOnlyList<CustomerImportSkip> Skipped,
    IReadOnlyList<string> Problems)
{
    public int ContactCount => ToAdd.Count(r => r.Contact is not null);
    public int InactiveCount => ToAdd.Count(r => !r.IsActive);
    public int WithoutContactCount => ToAdd.Count(r => r.Contact is null);
}

public sealed record CustomerImportResult(int CustomersAdded, int ContactsAdded, IReadOnlyList<CustomerImportSkip> Skipped);

/// <summary>A customer already in the app, as the import matches against it.</summary>
public sealed record ExistingCustomerKey(string? AccountCode, string Name);

public interface ICustomerImportService
{
    /// <summary>Works out what importing the listing would add and skip. Saves nothing.</summary>
    Task<CustomerImportPlan> PlanAsync(PastelListing listing, CancellationToken ct = default);

    /// <summary>Adds the planned customers, skipping any that were added since the plan was made.</summary>
    Task<CustomerImportResult> ImportAsync(CustomerImportPlan plan, CancellationToken ct = default);
}

/// <summary>
/// Brings the client's customers in from the Sage Pastel Customer Listing, which the
/// client asked for. The rules below were chosen by the team; change them here if the
/// client wants them otherwise:
///
/// - A customer already in the app is never changed. It is matched on its Pastel
///   account code, or on its name when it was added by hand without a code, so an
///   older export never overwrites what was edited in the app and the import can be
///   run again after a later export.
/// - The address is the postal address, as printed on the Pastel invoice, or the
///   delivery address when there is no postal one.
/// - The contact is the person Pastel names, with its email and phone numbers. A
///   customer with no contact name gets no contact; one is added the first time the
///   customer is quoted, rather than a made-up name.
/// - A customer blocked in Pastel comes in deactivated.
///
/// Every customer is checked by the same rules as one typed in on screen, and the
/// changes go through the audit trail like any other.
/// </summary>
public sealed partial class CustomerImportService : ICustomerImportService
{
    /// <summary>Customers saved per database round trip, so a large listing does not
    /// become one very long transaction.</summary>
    public const int BatchSize = 100;

    private readonly ICustomerRepository _customers;

    public CustomerImportService(ICustomerRepository customers) => _customers = customers;

    public async Task<CustomerImportPlan> PlanAsync(PastelListing listing, CancellationToken ct = default)
    {
        var existing = new Matcher(await _customers.ExistingKeysAsync(ct));
        var toAdd = new List<CustomerImportRow>();
        var skipped = new List<CustomerImportSkip>();
        var seenInFile = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var record in listing.Customers)
        {
            var code = record.AccountCode.Trim().ToUpperInvariant();

            if (!seenInFile.Add(code))
            {
                skipped.Add(new(code, record.Name, "The account code appears more than once in the file; only the first is imported."));
                continue;
            }

            if (existing.Reason(code, record.Name) is { } reason)
            {
                skipped.Add(new(code, record.Name, reason));
                continue;
            }

            var customer = CustomerService.Normalise(ToCustomer(record));
            if (CustomerService.Validate(customer) is { } errors)
            {
                skipped.Add(new(code, record.Name, Describe(errors)));
                continue;
            }

            var notes = new List<string>();
            var contact = ToContact(record, notes);
            if (!record.Blocked && contact is null)
                notes.Add("No contact person in Pastel. Add one before quoting this customer.");
            if (record.Blocked)
                notes.Add("Blocked in Pastel, so imported as inactive.");

            existing.Remember(code);
            toAdd.Add(new CustomerImportRow(customer, contact, !record.Blocked, notes));
        }

        return new CustomerImportPlan(toAdd, skipped, listing.Problems);
    }

    public async Task<CustomerImportResult> ImportAsync(CustomerImportPlan plan, CancellationToken ct = default)
    {
        // Checked again: the plan was made on an earlier request, and a customer may
        // have been added on screen since.
        var existing = new Matcher(await _customers.ExistingKeysAsync(ct));
        var skipped = new List<CustomerImportSkip>(plan.Skipped);
        int customersAdded = 0, contactsAdded = 0, inBatch = 0;

        foreach (var row in plan.ToAdd)
        {
            var code = row.Customer.AccountCode!;
            if (existing.Reason(code, row.Customer.Name) is { } reason)
            {
                skipped.Add(new(code, row.Customer.Name, reason));
                continue;
            }

            var customer = new Customer
            {
                Name = row.Customer.Name,
                AccountCode = code,
                AddressLine1 = row.Customer.AddressLine1,
                AddressLine2 = row.Customer.AddressLine2,
                City = row.Customer.City,
                PostalCode = row.Customer.PostalCode,
                VatNumber = row.Customer.VatNumber,
                IsActive = row.IsActive
            };
            if (row.Contact is { } c)
            {
                customer.Contacts.Add(new Contact { FullName = c.FullName, Email = c.Email, Phone = c.Phone, Position = c.Position });
                contactsAdded++;
            }

            _customers.Add(customer);
            existing.Remember(code);
            customersAdded++;

            if (++inBatch == BatchSize)
            {
                await _customers.SaveChangesAsync(ct);
                inBatch = 0;
            }
        }

        if (inBatch > 0)
            await _customers.SaveChangesAsync(ct);

        return new CustomerImportResult(customersAdded, contactsAdded, skipped);
    }

    // ---- From a Pastel record to the app's fields ----

    private static CustomerInput ToCustomer(PastelCustomerRecord r)
    {
        var lines = r.PostalAddress.Count > 0 ? r.PostalAddress : r.DeliveryAddress;
        var address = SplitAddress(lines);
        return new CustomerInput(r.Name, r.AccountCode, address.Line1, address.Line2, address.City, address.PostalCode, r.TaxReference);
    }

    private static ContactInput? ToContact(PastelCustomerRecord r, List<string> notes)
    {
        if (r.Contact is null)
            return null;

        var email = r.Email;
        var phone = string.Join(" / ", new[] { r.Telephone, r.MobilePhone }.Where(p => p is not null));
        var contact = CustomerService.Normalise(new ContactInput(r.Contact, email, phone));

        if (CustomerService.Validate(contact) is { } errors && errors.ContainsKey(nameof(ContactInput.Email)))
        {
            notes.Add($"The email in Pastel (\"{email}\") is not one valid address, so it was left off the contact.");
            contact = contact with { Email = null };
        }

        return CustomerService.Validate(contact) is null ? contact : null;
    }

    /// <summary>
    /// Pastel holds an address as up to five free lines. The last line usually ends in
    /// the four-digit postal code ("Chempet 7442"); otherwise it is taken as the town.
    /// The first line left is line 1, and any others are joined into line 2, so no
    /// part of the address is lost.
    /// </summary>
    public static (string? Line1, string? Line2, string? City, string? PostalCode) SplitAddress(IReadOnlyList<string> lines)
    {
        var rest = lines.Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? city = null, postalCode = null;

        if (rest.Count > 0 && PostalCodeAtEnd().Match(rest[^1]) is { Success: true } m)
        {
            postalCode = m.Groups["code"].Value;
            var town = m.Groups["town"].Value.Trim().TrimEnd(',').Trim();
            rest.RemoveAt(rest.Count - 1);
            if (town.Length > 0)
                city = town;
            else if (rest.Count > 1)
            {
                city = rest[^1];
                rest.RemoveAt(rest.Count - 1);
            }
        }
        else if (rest.Count > 1)
        {
            city = rest[^1];
            rest.RemoveAt(rest.Count - 1);
        }

        var line1 = rest.Count > 0 ? rest[0] : null;
        var line2 = rest.Count > 1 ? string.Join(", ", rest.Skip(1)) : null;
        return (line1, line2, city, postalCode);
    }

    [GeneratedRegex(@"^(?<town>.*?)[\s,]*(?<code>\d{4})$")]
    private static partial Regex PostalCodeAtEnd();

    private static string Describe(Dictionary<string, string[]> errors) =>
        string.Join(" ", errors.Select(e => e.Key switch
        {
            nameof(CustomerInput.Name) when e.Value[0].StartsWith("A customer needs") => "There is no name in Pastel.",
            _ => $"{e.Key}: {e.Value[0]}"
        }));

    /// <summary>The customers already in the app, and those planned so far.</summary>
    private sealed class Matcher
    {
        private readonly HashSet<string> _codes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _namesWithoutCode = new(StringComparer.OrdinalIgnoreCase);

        public Matcher(IEnumerable<ExistingCustomerKey> existing)
        {
            foreach (var k in existing)
            {
                if (string.IsNullOrWhiteSpace(k.AccountCode))
                    _namesWithoutCode.Add(k.Name.Trim());
                else
                    _codes.Add(k.AccountCode.Trim());
            }
        }

        public string? Reason(string code, string name) =>
            _codes.Contains(code) ? "Already in the app with this account code. Left as it is."
            : _namesWithoutCode.Contains(name.Trim()) ? "Already in the app under this name, with no account code. Left as it is; add the code on its page."
            : null;

        public void Remember(string code) => _codes.Add(code);
    }
}
