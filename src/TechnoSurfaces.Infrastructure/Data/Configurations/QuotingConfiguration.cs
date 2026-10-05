using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechnoSurfaces.Domain.Auditing;
using TechnoSurfaces.Domain.Catalogue;
using TechnoSurfaces.Domain.People;
using TechnoSurfaces.Domain.Quoting;

namespace TechnoSurfaces.Infrastructure.Data.Configurations;

public sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> e)
    {
        e.Property(x => x.Name).HasMaxLength(200).IsRequired();
        e.Property(x => x.AccountCode).HasMaxLength(40);
        e.Property(x => x.AddressLine1).HasMaxLength(200);
        e.Property(x => x.AddressLine2).HasMaxLength(200);
        e.Property(x => x.City).HasMaxLength(100);
        e.Property(x => x.PostalCode).HasMaxLength(20);
        e.Property(x => x.VatNumber).HasMaxLength(40);
        e.HasIndex(x => x.Name);
    }
}

public sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> e)
    {
        e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        e.Property(x => x.Email).HasMaxLength(200);
        e.Property(x => x.Phone).HasMaxLength(40);
        e.Property(x => x.Position).HasMaxLength(100);

        // Some customers have four or five estimators sending through requests.
        e.HasOne(x => x.Customer)
            .WithMany(c => c.Contacts)
            .HasForeignKey(x => x.CustomerId)
            .OnDelete(DeleteBehavior.NoAction);
    }
}

public sealed class AppUserConfiguration : IEntityTypeConfiguration<AppUser>
{
    public void Configure(EntityTypeBuilder<AppUser> e)
    {
        e.Property(x => x.Id).HasMaxLength(450);
        e.Property(x => x.UserName).HasMaxLength(100).IsRequired();
        e.Property(x => x.FullName).HasMaxLength(200).IsRequired();
        e.Property(x => x.Email).HasMaxLength(200);
        e.Ignore(x => x.IsManagingDirector);

        e.HasIndex(x => x.UserName).IsUnique();
    }
}

public sealed class QuoteConfiguration : IEntityTypeConfiguration<Quote>
{
    public void Configure(EntityTypeBuilder<Quote> e)
    {
        e.Property(x => x.Reference).HasMaxLength(40).IsRequired();
        e.Property(x => x.Site).HasMaxLength(200);
        e.Property(x => x.Project).HasMaxLength(200);
        e.Property(x => x.CustomerReference).HasMaxLength(100);
        e.Property(x => x.DeliveryAddress).HasMaxLength(300);
        e.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
        e.Property(x => x.ApprovedByUserId).HasMaxLength(450);

        e.Ignore(x => x.CurrentVersion);
        e.Ignore(x => x.OriginalVersion);

        e.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.NoAction);
        e.HasOne(x => x.Contact).WithMany().HasForeignKey(x => x.ContactId).OnDelete(DeleteBehavior.NoAction);

        e.HasMany(x => x.Versions)
            .WithOne(v => v.Quote!)
            .HasForeignKey(v => v.QuoteId)
            .OnDelete(DeleteBehavior.NoAction);
        e.Navigation(x => x.Versions).UsePropertyAccessMode(PropertyAccessMode.Field);

        // One reference identifies one quote.
        e.HasIndex(x => x.Reference).IsUnique();

        // Supports the approval queue and the quote listing screen.
        e.HasIndex(x => new { x.Status, x.IssueDate });
    }
}

public sealed class QuoteVersionConfiguration : IEntityTypeConfiguration<QuoteVersion>
{
    public void Configure(EntityTypeBuilder<QuoteVersion> e)
    {
        e.Property(x => x.CreatedByUserId).HasMaxLength(450).IsRequired();
        e.Property(x => x.MarkupPercent).HasPrecision(5, 2);
        e.Property(x => x.VatRate).HasPrecision(5, 4);
        e.Property(x => x.TransportAmount).HasPrecision(18, 2);

        e.HasMany(x => x.CostingLines)
            .WithOne()
            .HasForeignKey(l => l.QuoteVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        e.Navigation(x => x.CostingLines).UsePropertyAccessMode(PropertyAccessMode.Field);

        e.HasMany(x => x.QuotationLines)
            .WithOne()
            .HasForeignKey(l => l.QuoteVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        e.Navigation(x => x.QuotationLines).UsePropertyAccessMode(PropertyAccessMode.Field);

        // The wording and warranties a version was approved with. They belong to
        // the version, like its lines, so an issued quotation can be reproduced
        // exactly after the standing terms change.
        e.HasMany(x => x.Terms)
            .WithOne()
            .HasForeignKey(t => t.QuoteVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        e.Navigation(x => x.Terms).UsePropertyAccessMode(PropertyAccessMode.Field);

        e.HasMany(x => x.Warranties)
            .WithOne()
            .HasForeignKey(w => w.QuoteVersionId)
            .OnDelete(DeleteBehavior.NoAction);
        e.Navigation(x => x.Warranties).UsePropertyAccessMode(PropertyAccessMode.Field);

        e.Ignore(x => x.HasRecordedTerms);
        e.Ignore(x => x.IsIssued);

        // The heading the version was issued with. Lengths match the columns the
        // values are copied from, so a copy can never be truncated.
        e.Property(x => x.IssuedByUserId).HasMaxLength(450);
        e.Property(x => x.IssuedAttention).HasMaxLength(200);
        e.Property(x => x.IssuedCompany).HasMaxLength(200);
        e.Property(x => x.IssuedTel).HasMaxLength(40);
        e.Property(x => x.IssuedEmail).HasMaxLength(200);
        e.Property(x => x.IssuedSite).HasMaxLength(200);
        e.Property(x => x.IssuedProject).HasMaxLength(200);
        e.Property(x => x.IssuedCustomerReference).HasMaxLength(100);

        // Version numbers are sequential within a quote.
        e.HasIndex(x => new { x.QuoteId, x.VersionNo }).IsUnique();
    }
}

public sealed class CostingLineConfiguration : IEntityTypeConfiguration<CostingLine>
{
    public void Configure(EntityTypeBuilder<CostingLine> e)
    {
        e.Property(x => x.Description).HasMaxLength(400).IsRequired();
        e.Property(x => x.PriceOrigin).HasMaxLength(400).IsRequired();
        e.Property(x => x.ResolvedUnitPrice).HasPrecision(18, 2);
        e.Property(x => x.SupplierDiscountPercent).HasPrecision(5, 2);
        e.Property(x => x.OverriddenUnitPrice).HasPrecision(18, 2);
        e.Ignore(x => x.UnitPrice);
        e.Ignore(x => x.HasPriceOverride);
        e.Ignore(x => x.IsDerived);

        // Not an integer: Woodcentre quote stock in half sheets, and area-derived
        // quantities are fractional.
        e.Property(x => x.Quantity).HasPrecision(18, 4);
        e.Property(x => x.SheetAreaM2).HasPrecision(18, 4);
        e.Property(x => x.DerivationFactor).HasPrecision(9, 4);

        // Records which price row was used. Optional because rate lines carry no
        // material. The price itself is copied onto the line, so this reference is
        // for traceability and a later change to the row cannot alter the quote.
        e.HasOne<MaterialPrice>()
            .WithMany()
            .HasForeignKey(x => x.MaterialPriceId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        // Optional for the same reason, inverted: material lines carry no rate item.
        e.HasOne<RateItem>()
            .WithMany()
            .HasForeignKey(x => x.RateItemId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.ToTable(t =>
        {
            // A costing line is either a material line or a rate line.
            t.HasCheckConstraint(
                "CK_CostingLine_MaterialOrRate",
                "([MaterialPriceId] IS NOT NULL AND [RateItemId] IS NULL) OR ([MaterialPriceId] IS NULL AND [RateItemId] IS NOT NULL)");

            // NFR-01: no line is priced at zero, whether the price came from the
            // catalogue or was typed on the quote.
            t.HasCheckConstraint("CK_CostingLine_PricePositive", "[ResolvedUnitPrice] > 0");
            t.HasCheckConstraint("CK_CostingLine_OverridePositive", "[OverriddenUnitPrice] IS NULL OR [OverriddenUnitPrice] > 0");
        });

        e.HasIndex(x => x.QuoteVersionId);
    }
}

public sealed class QuotationLineConfiguration : IEntityTypeConfiguration<QuotationLine>
{
    public void Configure(EntityTypeBuilder<QuotationLine> e)
    {
        e.Property(x => x.Description).HasMaxLength(400).IsRequired();
        e.Property(x => x.Room).HasMaxLength(120);
        e.Property(x => x.AmountExVat).HasPrecision(18, 2);
        e.Property(x => x.Quantity).HasPrecision(18, 4);

        e.HasIndex(x => x.QuoteVersionId);
    }
}

public sealed class InvoiceRecordConfiguration : IEntityTypeConfiguration<InvoiceRecord>
{
    public void Configure(EntityTypeBuilder<InvoiceRecord> e)
    {
        e.Property(x => x.InvoiceNumber).HasMaxLength(40).IsRequired();
        e.Property(x => x.AmountIncVat).HasPrecision(18, 2);
        e.Property(x => x.RecordedByUserId).HasMaxLength(450).IsRequired();

        // An accepted quote has at most one tax invoice, and none until one is raised.
        e.HasOne(x => x.Quote)
            .WithOne()
            .HasForeignKey<InvoiceRecord>(x => x.QuoteId)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => x.InvoiceNumber).IsUnique();
    }
}

public sealed class AuditEntryConfiguration : IEntityTypeConfiguration<AuditEntry>
{
    public void Configure(EntityTypeBuilder<AuditEntry> e)
    {
        e.Property(x => x.EntityName).HasMaxLength(100).IsRequired();
        e.Property(x => x.EntityKey).HasMaxLength(100).IsRequired();
        e.Property(x => x.PropertyName).HasMaxLength(100).IsRequired();
        e.Property(x => x.OldValue).HasMaxLength(1000);
        e.Property(x => x.NewValue).HasMaxLength(1000);
        e.Property(x => x.UserId).HasMaxLength(450).IsRequired();

        e.HasIndex(x => new { x.EntityName, x.EntityKey });
        e.HasIndex(x => x.ChangedAtUtc);
    }
}

public sealed class QuotationTermConfiguration : IEntityTypeConfiguration<QuotationTerm>
{
    public void Configure(EntityTypeBuilder<QuotationTerm> e)
    {
        e.Property(x => x.Text).HasMaxLength(500).IsRequired();

        e.ToTable(t => t.HasCheckConstraint("CK_QuotationTerm_TextNotBlank", "[Text] <> ''"));

        // The quotation reads the active lines of every section in order.
        e.HasIndex(x => new { x.IsActive, x.Section, x.SortOrder });
    }
}

public sealed class QuoteVersionTermConfiguration : IEntityTypeConfiguration<QuoteVersionTerm>
{
    public void Configure(EntityTypeBuilder<QuoteVersionTerm> e)
    {
        e.Property(x => x.Text).HasMaxLength(500).IsRequired();
        e.HasIndex(x => new { x.QuoteVersionId, x.Section, x.SortOrder });
    }
}

public sealed class QuoteVersionWarrantyConfiguration : IEntityTypeConfiguration<QuoteVersionWarranty>
{
    public void Configure(EntityTypeBuilder<QuoteVersionWarranty> e)
    {
        e.Property(x => x.Brand).HasMaxLength(120).IsRequired();
        e.Property(x => x.MaterialWarranty).HasMaxLength(60).IsRequired();
        e.Property(x => x.WorkmanshipWarranty).HasMaxLength(60).IsRequired();

        // One warranty per brand on a version.
        e.HasIndex(x => new { x.QuoteVersionId, x.Brand }).IsUnique();
    }
}
