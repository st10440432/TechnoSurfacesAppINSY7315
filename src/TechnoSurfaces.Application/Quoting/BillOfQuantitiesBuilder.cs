using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Application.Quoting;

/// <summary>
/// The customer quotation as a bill of quantities, built from the items on the
/// costing sheet: one row per item at its rate, the price of one of it with its own
/// markup, and the amount for the number needed. Nothing is typed, so the bill always
/// adds up to the costing total.
///
/// Transport and the allowance are for the whole job. As the client prices a tender,
/// they are spread over the items in proportion to each item's amount, so every rate
/// carries its share; or, when the estimator chooses, they are listed after the items.
/// </summary>
internal static class BillOfQuantitiesBuilder
{
    /// <summary>The wording of the transport line when it is listed on its own.</summary>
    internal const string TransportDescription = "Transport";

    // The heading over the amounts for the whole job, named for what is under it.
    internal const string AllowancesHeading = "Allowances";
    internal const string TransportHeading = "Transport";
    internal const string AllowancesAndTransportHeading = "Allowances and transport";

    private sealed class Row(QuoteItem item, decimal rate, decimal amount)
    {
        public QuoteItem Item { get; } = item;
        public decimal Rate { get; set; } = rate;
        public decimal Amount { get; set; } = amount;
    }

    internal static CustomerBill Build(QuoteVersion version, QuoteTotals totals)
    {
        // The totals list the items in the version's order, so they are paired by
        // position: an item not saved yet has no id to match on.
        var rows = version.Items.Zip(totals.Items, (item, figures) => new Row(item, figures.TotalExVat, figures.AmountExVat)).ToList();

        var extras = new List<CustomerBillExtra>();
        var itemsTotal = rows.Sum(r => r.Amount);
        if (version.SpreadAllowances && version.JobExtras() > 0 && itemsTotal > 0)
        {
            Spread(rows, version.JobExtras(), itemsTotal);
        }
        else
        {
            if (version.AllowanceAmount > 0)
                extras.Add(new(version.AllowanceWording, version.AllowanceAmount));
            if (version.TransportAmount > 0)
                extras.Add(new(TransportDescription, version.TransportAmount));
        }

        // Items next to each other under the same heading are one section, in the
        // order the items are listed.
        var sections = new List<(string? Heading, List<CustomerBillItem> Items)>();
        foreach (var row in rows)
        {
            if (sections.Count == 0 || sections[^1].Heading != row.Item.Section)
                sections.Add((row.Item.Section, new List<CustomerBillItem>()));
            sections[^1].Items.Add(new CustomerBillItem(row.Item.Number, row.Item.Name, row.Item.Unit, row.Item.Quantity, row.Rate, row.Amount));
        }

        var extrasHeading = (version.AllowanceAmount > 0 && extras.Count > 0, version.TransportAmount > 0 && extras.Count > 0) switch
        {
            (true, true) => AllowancesAndTransportHeading,
            (true, false) => AllowancesHeading,
            (false, true) => TransportHeading,
            _ => null
        };

        return new CustomerBill(sections.Select(s => new CustomerBillSection(s.Heading, s.Items)).ToList(), extras, extrasHeading);
    }

    /// <summary>What the bill adds up to before VAT.</summary>
    internal static decimal Total(CustomerBill bill) =>
        bill.Sections.Sum(s => s.Items.Sum(i => i.AmountExVat)) + bill.Extras.Sum(e => e.AmountExVat);

    /// <summary>
    /// Adds each item's share of the job's extras to its rate: the share of one is the
    /// rate's part of the items total, so an item's amount takes the same part of the
    /// extras. The amount stays the rate for the quantity. The cents lost to rounding
    /// go on the rate of the last item needed once, so the bill adds up to the costing
    /// to the cent; with no such item, on the last item's amount.
    /// </summary>
    private static void Spread(List<Row> rows, decimal extras, decimal itemsTotal)
    {
        foreach (var row in rows)
        {
            row.Rate = Round(row.Rate + row.Rate * extras / itemsTotal);
            row.Amount = Round(row.Rate * row.Item.Quantity);
        }

        var remainder = itemsTotal + extras - rows.Sum(r => r.Amount);
        if (remainder == 0)
            return;

        var priced = rows.Where(r => r.Amount > 0).ToList();
        if (priced.LastOrDefault(r => r.Item.Quantity == 1) is { } once)
        {
            once.Rate += remainder;
            once.Amount += remainder;
        }
        else
        {
            priced[^1].Amount += remainder;
        }
    }

    private static decimal Round(decimal value) =>
        decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

/// <summary>
/// What the customer quotation adds up to, in either layout, and whether it has
/// anything on it: the written lines, or the bill of quantities built from the items.
/// Shared by the quotation screen's check and the approval rule (US-10).
/// </summary>
internal static class CustomerQuotationTotal
{
    internal static decimal Of(QuoteVersion version, QuoteTotals totals) =>
        version.Layout == QuotationLayout.BillOfQuantities
            ? BillOfQuantitiesBuilder.Total(BillOfQuantitiesBuilder.Build(version, totals))
            : version.QuotationSubtotalExVat();

    internal static bool HasContent(QuoteVersion version, QuoteTotals totals) =>
        version.Layout == QuotationLayout.BillOfQuantities
            ? totals.TotalExVat > 0
            : version.QuotationLines.Count > 0;
}
