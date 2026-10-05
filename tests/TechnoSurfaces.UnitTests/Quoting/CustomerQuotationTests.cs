using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Costing;
using TechnoSurfaces.Application.Quoting;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Seed;

namespace TechnoSurfaces.UnitTests.Quoting;

/// <summary>
/// The customer quotation (US-10 to US-14) on SQLite with the seeded catalogue,
/// terms and warranties. The first test is the confidentiality rule: nothing on the
/// customer document can carry a cost, discount, markup or price origin (US-11).
/// </summary>
public sealed class CustomerQuotationTests : IAsyncLifetime
{
    private SqliteConnection _connection = null!;
    private DbContextOptions<TechnoSurfacesDbContext> _options = null!;
    private int _quoteId;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        _options = new DbContextOptionsBuilder<TechnoSurfacesDbContext>().UseSqlite(_connection).Options;

        await using var db = new TechnoSurfacesDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await CatalogueSeeder.SeedAsync(db);
        await RateCardSeeder.SeedAsync(db);
        await QuotationTermsSeeder.SeedAsync(db);
        await CustomerSeeder.SeedAsync(db);

        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var infinitoPrice = await db.MaterialPrices
            .Include(p => p.PriceBand).ThenInclude(b => b!.ProductLine)
            .FirstAsync(p => p.PriceBand != null && p.PriceBand.ProductLine!.Name.StartsWith("Infinito"));

        var quote = new Quote("TS-QTN-1", customer.Id, customer.Contacts.Single().Id, "estimator", new DateOnly(2026, 10, 4));
        quote.UpdateDetails("Tokai", "Kitchen", "SWEET VALLEY FARM", null);
        var version = quote.StartNewVersion("estimator", markupPercent: 50m);
        version.AddCostingLine(CostingLine.ForMaterial(infinitoPrice.Id, "Infinito material line", 1000m,
            "Surface Studio price band A1, effective 2026-03-01", 1m, 2.7968m));
        version.AddCostingLine(CostingLine.ForRate(1, "Sanding time", 100m, "Rate card: Sanding time", 2m, isBelowTheLine: false));
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        _quoteId = quote.Id;
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<T> WithServiceAsync<T>(Func<QuotationGenerationService, Task<T>> act)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        return await act(new QuotationGenerationService(new QuoteRepository(db), new QuotationTermsReader(db), new QuoteCalculationService()));
    }

    /// <summary>Every type the customer document is made of, nested types included.</summary>
    private static IEnumerable<Type> DocumentTypes()
    {
        var seen = new HashSet<Type>();
        var pending = new Stack<Type>(new[] { typeof(CustomerQuotation) });
        while (pending.Count > 0)
        {
            var type = pending.Pop();
            if (!seen.Add(type))
                continue;
            yield return type;

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var propertyType = property.PropertyType;
                var element = propertyType.IsGenericType ? propertyType.GetGenericArguments()[0] : propertyType;
                if (element.Namespace == typeof(CustomerQuotation).Namespace)
                    pending.Push(element);
            }
        }
    }

    [Fact]
    public void Nothing_on_the_customer_document_can_carry_a_cost_discount_markup_or_origin()
    {
        string[] forbidden = { "Cost", "Discount", "Markup", "UnitPrice", "Resolved", "Origin", "Supplier", "Rate" };

        var offending = DocumentTypes()
            .SelectMany(t => t.GetProperties().Select(p => $"{t.Name}.{p.Name}"))
            .Where(name => forbidden.Any(word => name.Split('.')[1].Contains(word, StringComparison.OrdinalIgnoreCase)
                                                 && !name.EndsWith(".VatRate")))
            .ToList();

        Assert.Empty(offending);
        Assert.Contains(typeof(CustomerQuotationLine), DocumentTypes());
    }

    [Fact]
    public async Task The_document_is_addressed_to_the_contact_and_billed_to_the_customer()
    {
        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Equal("Accounts", document!.Header.Attention);
        Assert.Equal("RA Woodcraft", document.Header.Company);
        Assert.Equal("TS-QTN-1", document.Header.Reference);
        Assert.Equal("Tokai", document.Header.Site);
        Assert.Equal("Kitchen", document.Header.Project);
        Assert.Equal("SWEET VALLEY FARM", document.Header.YourRef);
        Assert.Equal(new DateOnly(2026, 11, 3), document.Header.ValidUntil);
    }

    [Fact]
    public async Task Lines_are_by_room_and_the_totals_add_vat_at_fifteen_per_cent()
    {
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Countertop, fabricate and install", 1000m, "Kitchen")));
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Vanity top", 500m, "Guest bathroom")));

        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Equal(new[] { "Kitchen", "Guest bathroom" }, document!.Lines.Select(l => l.Room));
        Assert.Equal(1500m, document.Totals.SubtotalExVat);
        Assert.Equal(225m, document.Totals.Vat);
        Assert.Equal(1725m, document.Totals.TotalIncVat);
    }

    [Fact]
    public async Task The_payload_holds_no_costing_figure_or_wording()
    {
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Countertop, fabricate and install", 1800m, "Kitchen")));

        var json = JsonSerializer.Serialize(await WithServiceAsync(s => s.GenerateAsync(_quoteId)));

        Assert.DoesNotContain("Sanding time", json);
        Assert.DoesNotContain("Rate card", json);
        Assert.DoesNotContain("price band", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1000", json);
        Assert.DoesNotContain("markup", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Standing_terms_appear_without_being_typed_and_missing_bank_details_are_flagged()
    {
        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Contains(document!.Terms, t => t.Section == nameof(TermSection.TermsAndConditions) && t.Lines.Count > 0);
        Assert.Contains(document.Terms, t => t.Section == nameof(TermSection.Exclusions));
        Assert.False(document.BankDetailsSet);
        Assert.False(document.IsIssued);
    }

    [Fact]
    public async Task A_brand_with_no_confirmed_warranty_prints_none()
    {
        // Infinito's warranty has not been confirmed by the client, so nothing is
        // printed for it rather than another brand's wording (US-13).
        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Empty(document!.Warranties);
    }

    [Fact]
    public async Task The_warranty_follows_the_brand_quoted()
    {
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var staronPrice = await db.MaterialPrices
                .Include(p => p.PriceBand).ThenInclude(b => b!.ProductLine)
                .FirstAsync(p => p.PriceBand != null && p.PriceBand.ProductLine!.Name == "Staron");
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            quote!.CurrentVersion!.AddCostingLine(CostingLine.ForMaterial(staronPrice.Id, "Staron material line", 500m,
                "Staron price band, effective 2025-03-01", 1m, 2.7968m));
            await db.SaveChangesAsync();
        }

        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        var warranty = Assert.Single(document!.Warranties);
        Assert.Equal(QuotationTermsSeeder.StaronBrand, warranty.Brand);
        Assert.Equal("10 years", warranty.Material);
        Assert.Equal("1 year", warranty.Workmanship);
    }

    [Fact]
    public async Task The_check_shows_how_far_the_quotation_is_from_the_costing()
    {
        // Costing: R1 000 material + R200 sanding = R1 200, plus 50% markup = R1 800.
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Countertop", 1500m, "Kitchen")));
        var under = await WithServiceAsync(s => s.CheckAsync(_quoteId));

        var lineId = (await WithServiceAsync(s => s.GenerateAsync(_quoteId)))!.Lines.Single().Id;
        var changed = await WithServiceAsync(s => s.ChangeLineAsync(_quoteId, lineId, new QuotationLineInput("Countertop", 1800m, "Kitchen")));

        Assert.Equal(1800m, under!.CostingTotalExVat);
        Assert.Equal(-300m, under.Difference);
        Assert.False(under.Matches);
        Assert.True(changed.Check!.Matches);
    }

    [Fact]
    public async Task Lines_can_be_reordered_and_removed()
    {
        var first = await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("First", 100m)));
        var second = await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Second", 200m)));

        await WithServiceAsync(s => s.ReorderAsync(_quoteId, new[] { second.Line!.Id, first.Line!.Id }));
        var reordered = await WithServiceAsync(s => s.GenerateAsync(_quoteId));
        await WithServiceAsync(s => s.RemoveLineAsync(_quoteId, second.Line!.Id));
        var removed = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Equal(new[] { "Second", "First" }, reordered!.Lines.Select(l => l.Description));
        Assert.Equal("First", Assert.Single(removed!.Lines).Description);
    }

    [Fact]
    public async Task A_reorder_must_name_every_line_once()
    {
        var first = await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("First", 100m)));
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Second", 200m)));

        var result = await WithServiceAsync(s => s.ReorderAsync(_quoteId, new[] { first.Line!.Id }));

        Assert.Equal(QuotationOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public async Task An_empty_description_and_a_negative_amount_are_refused()
    {
        var result = await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("  ", -1m)));

        Assert.Equal(QuotationOutcome.Invalid, result.Outcome);
        Assert.Contains(nameof(QuotationLineInput.Description), result.Errors!.Keys);
        Assert.Contains(nameof(QuotationLineInput.AmountExVat), result.Errors.Keys);
    }

    [Fact]
    public async Task An_issued_quotation_keeps_the_wording_it_was_approved_with()
    {
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Countertop", 1800m, "Kitchen")));
        await using (var db = new TechnoSurfacesDbContext(_options))
        {
            var quote = await new QuoteRepository(db).GetAsync(_quoteId);
            await new QuoteTermsRecorder(new QuotationTermsReader(db)).RecordAsync(quote!.CurrentVersion!);
            quote.Submit();
            quote.Approve("md");
            await db.SaveChangesAsync();

            // The wording changes after the quote was issued.
            foreach (var term in await db.QuotationTerms.Where(t => t.Section == TermSection.Exclusions).ToListAsync())
                term.Text = "Changed after issue";
            await db.SaveChangesAsync();
        }

        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));
        var refused = await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Late line", 1m)));

        Assert.True(document!.IsIssued);
        Assert.DoesNotContain("Changed after issue", document.Terms.Single(t => t.Section == nameof(TermSection.Exclusions)).Lines);
        Assert.Equal(QuotationOutcome.VersionSealed, refused.Outcome);
    }

    /// <summary>A second quote with only the given material lines, each one sheet at its price.</summary>
    private async Task<int> AddMaterialOnlyQuoteAsync(string reference, decimal markupPercent, params decimal[] sheetPrices)
    {
        await using var db = new TechnoSurfacesDbContext(_options);
        var customer = await db.Customers.Include(c => c.Contacts).SingleAsync();
        var price = await db.MaterialPrices.FirstAsync(p => p.PriceBand != null);

        var quote = new Quote(reference, customer.Id, customer.Contacts.Single().Id, "estimator", new DateOnly(2026, 10, 4));
        var version = quote.StartNewVersion("estimator", markupPercent);
        for (var i = 0; i < sheetPrices.Length; i++)
            version.AddCostingLine(CostingLine.ForMaterial(price.Id, $"Material {i + 1}", sheetPrices[i], "Price band", 1m, 2.7968m));
        db.Quotes.Add(quote);
        await db.SaveChangesAsync();
        return quote.Id;
    }

    [Fact]
    public async Task Copying_the_costing_gives_a_line_per_material_and_one_for_the_rest_that_add_up_to_it()
    {
        // Costing: R1 000 material + R200 sanding = R1 200, plus 50% markup = R1 800.
        var result = await WithServiceAsync(s => s.AddLinesFromCostingAsync(_quoteId));
        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Equal(QuotationOutcome.Ok, result.Outcome);
        Assert.True(result.Check!.Matches);
        Assert.Collection(document!.Lines,
            material =>
            {
                Assert.Equal("Material", material.Room);
                Assert.Equal("Infinito material line", material.Description);
                Assert.Equal(1m, material.Quantity);
                Assert.Equal(1500m, material.AmountExVat);
            },
            rest =>
            {
                Assert.Equal("Labour and extras", rest.Room);
                Assert.Equal(QuotationGenerationService.OtherCostsDescription, rest.Description);
                Assert.Equal(300m, rest.AmountExVat);
            });
    }

    [Fact]
    public async Task Every_copied_material_carries_the_material_item()
    {
        var quoteId = await AddMaterialOnlyQuoteAsync("TS-QTN-ITEMS", 20m, 1000m, 2000m);

        await WithServiceAsync(s => s.AddLinesFromCostingAsync(quoteId));
        var document = await WithServiceAsync(s => s.GenerateAsync(quoteId));

        Assert.Equal(2, document!.Lines.Count);
        Assert.All(document.Lines, line => Assert.Equal(QuotationGenerationService.MaterialItem, line.Room));
    }

    [Fact]
    public async Task Copying_the_costing_puts_no_cost_or_price_origin_on_the_document()
    {
        await WithServiceAsync(s => s.AddLinesFromCostingAsync(_quoteId));

        var json = JsonSerializer.Serialize(await WithServiceAsync(s => s.GenerateAsync(_quoteId)));

        Assert.DoesNotContain("Sanding time", json);
        Assert.DoesNotContain("Rate card", json);
        Assert.DoesNotContain("price band", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("1000", json);
        Assert.DoesNotContain("markup", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Copying_the_costing_keeps_the_lines_already_written_and_adds_after_them()
    {
        await WithServiceAsync(s => s.AddLineAsync(_quoteId, new QuotationLineInput("Written by hand", 100m, "Kitchen")));

        var result = await WithServiceAsync(s => s.AddLinesFromCostingAsync(_quoteId));
        var document = await WithServiceAsync(s => s.GenerateAsync(_quoteId));

        Assert.Equal(new[] { "Written by hand", "Infinito material line", QuotationGenerationService.OtherCostsDescription },
            document!.Lines.Select(l => l.Description));
        Assert.False(result.Check!.Matches);
        Assert.Equal(100m, result.Check.Difference);
    }

    [Fact]
    public async Task Copying_materials_only_puts_the_rounding_on_the_last_material()
    {
        // Each R333,33 sheet marked up 12,5% is R374,99625, so R375,00 a line once
        // rounded. The costing totals R999,99 + R125,00 = R1 124,99, a cent less.
        var quoteId = await AddMaterialOnlyQuoteAsync("TS-QTN-ROUND", 12.5m, 333.33m, 333.33m, 333.33m);

        var result = await WithServiceAsync(s => s.AddLinesFromCostingAsync(quoteId));
        var document = await WithServiceAsync(s => s.GenerateAsync(quoteId));

        Assert.True(result.Check!.Matches);
        Assert.Equal(1124.99m, document!.Totals.SubtotalExVat);
        Assert.Equal(new[] { 375.00m, 375.00m, 374.99m }, document.Lines.Select(l => l.AmountExVat));
        Assert.DoesNotContain(document.Lines, l => l.Description == QuotationGenerationService.OtherCostsDescription);
    }

    [Fact]
    public async Task Copying_an_empty_costing_is_refused()
    {
        var quoteId = await AddMaterialOnlyQuoteAsync("TS-QTN-EMPTY", 35m);

        var result = await WithServiceAsync(s => s.AddLinesFromCostingAsync(quoteId));

        Assert.Equal(QuotationOutcome.Invalid, result.Outcome);
        Assert.Empty((await WithServiceAsync(s => s.GenerateAsync(quoteId)))!.Lines);
    }

    [Fact]
    public async Task An_unknown_quote_has_no_document()
    {
        Assert.Null(await WithServiceAsync(s => s.GenerateAsync(999)));
        Assert.Equal(QuotationOutcome.QuoteNotFound,
            (await WithServiceAsync(s => s.AddLineAsync(999, new QuotationLineInput("Line", 1m)))).Outcome);
    }
}
