using System.Globalization;
using TechnoSurfaces.Application.Auditing;

namespace TechnoSurfacesApp.Helpers;

/// <summary>One change as a person reads it: who, when, and a sentence saying what happened.</summary>
public sealed record ChangeStory(
    DateTime WhenLocal, string UserName, string Summary, IReadOnlyList<string> Details, bool IsPriceChange);

/// <summary>
/// Turns audit rows, which hold one property of one record each, into sentences. The
/// audit table records every column, including keys and timestamps a user never sees,
/// so those are left out here and the rest are named and formatted the way the
/// screens show them. Rows saved together for one record become one sentence.
/// </summary>
public static class ChangeStories
{
    // Columns that only matter to the database.
    private static readonly HashSet<string> Hidden = new(StringComparer.Ordinal)
    {
        "Id", "SortOrder", "CreatedAtUtc", "CreatedByUserId", "CapturedAtUtc", "CapturedByUserId",
        "RecordedAtUtc", "RecordedByUserId", "ApprovedByUserId", "ApprovedAtUtc", "SheetAreaM2",
        "DerivationFactor", "Derivation", "IsQuantityOverridden", "IsBelowTheLine", "LineType",
        "PriceOrigin", "VatRate", "ConcurrencyStamp", "SecurityStamp", "PasswordHash",
        "NormalizedEmail", "NormalizedUserName", "LockoutEnd", "AccessFailedCount"
    };

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["Quantity"] = "quantity",
        ["ResolvedUnitPrice"] = "price",
        ["OverriddenUnitPrice"] = "rate typed on the quote",
        ["SupplierDiscountPercent"] = "supplier discount",
        ["MarkupPercent"] = "markup",
        ["TransportAmount"] = "transport",
        ["Status"] = "status",
        ["Site"] = "site",
        ["Project"] = "project",
        ["CustomerReference"] = "customer's reference",
        ["DeliveryAddress"] = "delivery address",
        ["ValidUntil"] = "valid until date",
        ["IssueDate"] = "issue date",
        ["IsSealed"] = "locked",
        ["Description"] = "description",
        ["Room"] = "item",
        ["AmountExVat"] = "amount",
        ["PricePerSqm"] = "price per m²",
        ["Amount"] = "rate",
        ["EffectiveFrom"] = "start date",
        ["EffectiveTo"] = "end date",
        ["Text"] = "wording",
        ["IsActive"] = "active",
        ["MaterialWarranty"] = "material warranty",
        ["WorkmanshipWarranty"] = "workmanship warranty",
        ["InvoiceNumber"] = "invoice number",
        ["InvoiceDate"] = "invoice date",
        ["AmountIncVat"] = "invoiced amount",
        ["FullName"] = "name",
        ["Email"] = "email address",
        ["Phone"] = "phone number",
        ["AccountCode"] = "Pastel account code"
    };

    private static readonly HashSet<string> Money = new(StringComparer.Ordinal)
        { "ResolvedUnitPrice", "OverriddenUnitPrice", "TransportAmount", "AmountExVat", "Amount", "AmountIncVat", "PricePerSqm" };

    private static readonly HashSet<string> Percent = new(StringComparer.Ordinal)
        { "MarkupPercent", "SupplierDiscountPercent" };

    private static readonly HashSet<string> Dates = new(StringComparer.Ordinal)
        { "ValidUntil", "IssueDate", "EffectiveFrom", "EffectiveTo", "InvoiceDate" };

    /// <summary>The record types, as the filter on the audit trail lists them.</summary>
    public static string RecordName(string entity) => entity switch
    {
        "Quote" => "Quotes",
        "QuoteVersion" => "Quote versions",
        "CostingLine" => "Costing lines",
        "QuotationLine" => "Quotation lines",
        "MaterialPrice" => "Material prices",
        "RatePrice" => "Rates",
        "RateItem" => "Rate card items",
        "QuotationTerm" => "Quotation terms",
        "Brand" => "Brand warranties",
        "Colour" => "Colours",
        "ProductLine" => "Product lines",
        "Supplier" => "Suppliers",
        "SheetSize" => "Sheet sizes",
        "PriceBand" => "Price bands",
        "AppUser" => "User accounts",
        "Customer" => "Customers",
        "Contact" => "Contacts",
        "InvoiceRecord" => "Invoices",
        _ => Words.Humanise(entity)
    };

    private static string Noun(string entity) => entity switch
    {
        "Quote" => "the quote",
        "QuoteVersion" => "the version",
        "CostingLine" => "the costing line",
        "QuotationLine" => "the quotation line",
        "MaterialPrice" => "a material price",
        "RatePrice" => "a rate",
        "RateItem" => "a rate card item",
        "QuotationTerm" => "a line of the quotation terms",
        "Brand" => "a brand warranty",
        "Colour" => "a colour",
        "ProductLine" => "a product line",
        "Supplier" => "a supplier",
        "SheetSize" => "a sheet size",
        "PriceBand" => "a price band",
        "AppUser" => "a user account",
        "Customer" => "a customer",
        "Contact" => "a contact",
        "InvoiceRecord" => "the invoice",
        _ => Words.Humanise(entity).ToLowerInvariant()
    };

    /// <summary>Newest first. Rows for one record saved in the same second become one story.</summary>
    public static IReadOnlyList<ChangeStory> From(IEnumerable<AuditRow> rows)
    {
        var all = rows.ToList();

        // A line is named by its description wherever one was recorded, so a later
        // change to "line 12" can say which line it was.
        var names = all
            .Where(r => r.PropertyName is "Description" or "Name" or "FullName" or "Reference" && r.NewValue is not null)
            .GroupBy(r => (r.EntityName, r.EntityKey))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.ChangedAtUtc).First().NewValue!);

        return all
            .GroupBy(r => (r.EntityName, r.EntityKey, r.UserId, Second: r.ChangedAtUtc.Ticks / TimeSpan.TicksPerSecond))
            .Select(g => Tell(g.ToList(), names.GetValueOrDefault((g.Key.EntityName, g.Key.EntityKey))))
            .Where(s => s is not null)
            .Select(s => s!)
            .OrderByDescending(s => s.WhenLocal)
            .ToList();
    }

    private static ChangeStory? Tell(List<AuditRow> group, string? name)
    {
        var first = group[0];
        var entity = first.EntityName;
        var priced = group.Any(r => r.IsPriceChange);
        var called = name is null ? Noun(entity) : $"{Noun(entity)} {name}";

        if (group.Any(r => r.IsDeletion))
            return new(first.ChangedAtLocal, first.UserName, $"Removed {called}.", [], priced);

        // The heading copied onto a version when it is approved repeats what the quote
        // already says; the approval itself is told by the status change.
        var visible = group.Where(r => !Hidden.Contains(r.PropertyName)
            && !r.PropertyName.EndsWith("Id", StringComparison.Ordinal)
            && !r.PropertyName.StartsWith("Issued", StringComparison.Ordinal)).ToList();
        var created = group.Count > 2 && group.All(r => r.OldValue is null);

        if (created)
            return new(first.ChangedAtLocal, first.UserName, Created(entity, group, name), [], priced);

        if (visible.Count == 0)
            return null;

        var details = visible.Select(r => Changed(r)).ToList();
        return details.Count == 1
            ? new(first.ChangedAtLocal, first.UserName, $"{Capital(details[0])} on {called}.", [], priced)
            : new(first.ChangedAtLocal, first.UserName, $"Changed {called}.", details.Select(Capital).ToList(), priced);
    }

    private static string Created(string entity, List<AuditRow> group, string? name)
    {
        string? Value(string property) =>
            group.FirstOrDefault(r => r.PropertyName == property)?.NewValue is { } v ? Format(property, v) : null;

        return entity switch
        {
            "Quote" => $"Created the quote {Value("Reference") ?? name}.",
            "QuoteVersion" => $"Started version {Value("VersionNo")} with a markup of {Value("MarkupPercent")}.",
            "CostingLine" => $"Added the costing line {name}: {Value("Quantity")} at {Value("ResolvedUnitPrice")} each.",
            "QuotationLine" => $"Added the quotation line {name}: {Value("AmountExVat")} ex VAT.",
            "MaterialPrice" => $"Set a material price of {Value("PricePerSqm")} per m² from {Value("EffectiveFrom")}.",
            "RatePrice" => $"Set a rate of {Value("Amount")} from {Value("EffectiveFrom")}.",
            "InvoiceRecord" => $"Recorded Pastel invoice {Value("InvoiceNumber")} for {Value("AmountIncVat")}.",
            "Customer" => $"Added the customer {name}.",
            "Contact" => $"Added the contact {name}.",
            "AppUser" => $"Created a user account for {name}.",
            "QuotationTerm" => $"Added a line to the quotation terms: \"{Value("Text")}\".",
            _ => name is null ? $"Added {Noun(entity)}." : $"Added {Noun(entity)} {name}."
        };
    }

    private static string Changed(AuditRow r)
    {
        var label = Labels.GetValueOrDefault(r.PropertyName) ?? Words.Humanise(r.PropertyName).ToLowerInvariant();

        if (r.PropertyName == "IsSealed")
            return r.NewValue == "True" ? "locked the version" : "unlocked the version";
        if (r.PropertyName == "IsActive")
            return r.NewValue == "True" ? "made active" : "made inactive";

        var to = r.NewValue is null ? "nothing" : Format(r.PropertyName, r.NewValue);
        return r.OldValue is null
            ? $"set the {label} to {to}"
            : $"changed the {label} from {Format(r.PropertyName, r.OldValue)} to {to}";
    }

    private static string Format(string property, string value)
    {
        if (Money.Contains(property) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var money))
            return Fmt.Rand(money);
        if (Percent.Contains(property) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var pct))
            return Fmt.Pct(pct);
        if (property == "Quantity" && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var qty))
            return Fmt.Qty(qty);
        if (Dates.Contains(property) && DateOnly.TryParse(value, CultureInfo.InvariantCulture, out var date))
            return Fmt.Date(date);
        if (property == "Status")
            return StatusText.Label(value);
        if (value is "True" or "False")
            return value == "True" ? "yes" : "no";
        return value;
    }

    private static string Capital(string text) =>
        text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
