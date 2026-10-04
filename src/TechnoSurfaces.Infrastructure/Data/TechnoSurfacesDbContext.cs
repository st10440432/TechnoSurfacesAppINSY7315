using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain.Auditing;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data;

/// <summary>
/// The application database.
///
/// Every constraint and index below is configured deliberately rather than left to
/// EF Core convention, because the integrity rules are the substance of the
/// requirement: a costing line must reference a price that was in force when the
/// quote was created, and a colour must belong to a product line that in turn
/// belongs to a supplier.
/// </summary>
public class TechnoSurfacesDbContext : DbContext
{
    public TechnoSurfacesDbContext(DbContextOptions<TechnoSurfacesDbContext> options) : base(options) { }

    public DbSet<Supplier> Suppliers => Set<Supplier>();
    public DbSet<ProductLine> ProductLines => Set<ProductLine>();
    public DbSet<SheetSize> SheetSizes => Set<SheetSize>();
    public DbSet<PriceBand> PriceBands => Set<PriceBand>();
    public DbSet<Brand> Brands => Set<Brand>();
    public DbSet<Colour> Colours => Set<Colour>();
    public DbSet<MaterialPrice> MaterialPrices => Set<MaterialPrice>();
    public DbSet<RateItem> RateItems => Set<RateItem>();
    public DbSet<RatePrice> RatePrices => Set<RatePrice>();

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<AppUser> Users => Set<AppUser>();

    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<QuoteVersion> QuoteVersions => Set<QuoteVersion>();
    public DbSet<CostingLine> CostingLines => Set<CostingLine>();
    public DbSet<QuotationLine> QuotationLines => Set<QuotationLine>();
    public DbSet<InvoiceRecord> InvoiceRecords => Set<InvoiceRecord>();
    public DbSet<QuotationTerm> QuotationTerms => Set<QuotationTerm>();
    public DbSet<QuoteVersionTerm> QuoteVersionTerms => Set<QuoteVersionTerm>();
    public DbSet<QuoteVersionWarranty> QuoteVersionWarranties => Set<QuoteVersionWarranty>();

    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.ApplyConfigurationsFromAssembly(typeof(TechnoSurfacesDbContext).Assembly);
        base.OnModelCreating(b);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // Money and percentages are decimal throughout. Fixing the precision here
        // stops EF Core silently truncating, which is the class of error the whole
        // system exists to remove.
        c.Properties<decimal>().HavePrecision(18, 2);
        base.ConfigureConventions(c);
    }
}
