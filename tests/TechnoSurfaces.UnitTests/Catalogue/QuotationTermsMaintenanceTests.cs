using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Application.Auditing;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.Quoting;
using TechnoSurfaces.Infrastructure.Data;
using TechnoSurfaces.Infrastructure.Data.Auditing;

namespace TechnoSurfaces.UnitTests.Catalogue;

/// <summary>US-12 and US-13: the MD maintains the quotation terms and brand warranties, and every change is audited.</summary>
public sealed class QuotationTermsMaintenanceTests : IAsyncLifetime
{
    private const string ManagingDirectorId = "md-user-id";
    private const string Deposit60 = "60% deposit on order, balance on completion.";

    private sealed class FixedUser : ICurrentUser
    {
        public string UserId => ManagingDirectorId;
    }

    private SqliteConnection _connection = null!;
    private TechnoSurfacesDbContext _db = null!;
    private CatalogueService _service = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        _db = new TechnoSurfacesDbContext(
            new DbContextOptionsBuilder<TechnoSurfacesDbContext>()
                .UseSqlite(_connection)
                .AddInterceptors(new AuditInterceptor(new FixedUser()))
                .Options);
        await _db.Database.EnsureCreatedAsync();

        _db.QuotationTerms.AddRange(
            new QuotationTerm { Section = TermSection.PaymentTerms, Text = Deposit60, SortOrder = 1 },
            new QuotationTerm { Section = TermSection.Exclusions, Text = "Any plumbing or electrical work", SortOrder = 1 });
        _db.Brands.Add(new Brand { Name = "Test Brand" });
        await _db.SaveChangesAsync();

        _service = new CatalogueService(_db, new PriceHistory(_db), new FixedUser());
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private Task<QuotationTerm> StoredAsync(TermSection section) =>
        _db.QuotationTerms.AsNoTracking().SingleAsync(t => t.Section == section);

    [Fact]
    public async Task A_new_line_goes_to_the_end_of_its_section()
    {
        var result = await _service.AddTermAsync(TermSection.Exclusions, "  Removal of existing worktops  ");

        Assert.True(result.Succeeded);
        var added = await _db.QuotationTerms.AsNoTracking().SingleAsync(t => t.Text == "Removal of existing worktops");
        Assert.Equal(2, added.SortOrder);
        Assert.True(added.IsActive);
    }

    [Fact]
    public async Task Editing_a_line_is_audited_with_the_old_and_new_wording()
    {
        var term = await StoredAsync(TermSection.PaymentTerms);

        var result = await _service.UpdateTermAsync(term.Id, "50% deposit on order, balance on completion.");

        Assert.True(result.Succeeded);
        var entry = await _db.AuditEntries.SingleAsync(a =>
            a.EntityName == nameof(QuotationTerm) && a.PropertyName == nameof(QuotationTerm.Text) && a.OldValue != null);
        Assert.Equal(Deposit60, entry.OldValue);
        Assert.Equal("50% deposit on order, balance on completion.", entry.NewValue);
        Assert.Equal(ManagingDirectorId, entry.UserId);
    }

    [Fact]
    public async Task Bank_details_entered_by_the_md_are_audited()
    {
        var result = await _service.AddTermAsync(TermSection.BankDetails, "Bank: Example Bank, account 000000");

        Assert.True(result.Succeeded);
        var term = await StoredAsync(TermSection.BankDetails);
        Assert.True(await _db.AuditEntries.AnyAsync(a =>
            a.EntityName == nameof(QuotationTerm) && a.EntityKey == term.Id.ToString() && a.UserId == ManagingDirectorId));
    }

    [Fact]
    public async Task Retiring_a_line_keeps_it_but_takes_it_off_new_quotations()
    {
        var term = await StoredAsync(TermSection.Exclusions);

        var result = await _service.RetireTermAsync(term.Id);

        Assert.True(result.Succeeded);
        Assert.False((await StoredAsync(TermSection.Exclusions)).IsActive);
    }

    [Fact]
    public async Task The_last_active_line_cannot_be_retired()
    {
        await _service.RetireTermAsync((await StoredAsync(TermSection.Exclusions)).Id);
        var last = await StoredAsync(TermSection.PaymentTerms);

        var result = await _service.RetireTermAsync(last.Id);

        Assert.False(result.Succeeded);
        Assert.True((await StoredAsync(TermSection.PaymentTerms)).IsActive);
    }

    [Fact]
    public async Task A_retired_line_cannot_be_edited()
    {
        var term = await StoredAsync(TermSection.Exclusions);
        await _service.RetireTermAsync(term.Id);

        var result = await _service.UpdateTermAsync(term.Id, "Changed after retirement");

        Assert.False(result.Succeeded);
    }

    [Fact]
    public async Task A_blank_or_overlong_line_is_refused()
    {
        Assert.False((await _service.AddTermAsync(TermSection.Notes, "   ")).Succeeded);
        Assert.False((await _service.AddTermAsync(TermSection.Notes, new string('x', 501))).Succeeded);
    }

    [Fact]
    public async Task A_warranty_needs_both_periods_or_neither()
    {
        var brand = await _db.Brands.AsNoTracking().SingleAsync();

        Assert.False((await _service.SetBrandWarrantyAsync(brand.Id, "10 years", null)).Succeeded);
        Assert.True((await _service.SetBrandWarrantyAsync(brand.Id, "10 years", "1 year")).Succeeded);
        Assert.True((await _service.SetBrandWarrantyAsync(brand.Id, " ", "")).Succeeded);

        var stored = await _db.Brands.AsNoTracking().SingleAsync();
        Assert.Null(stored.MaterialWarranty);
        Assert.Null(stored.WorkmanshipWarranty);
    }
}