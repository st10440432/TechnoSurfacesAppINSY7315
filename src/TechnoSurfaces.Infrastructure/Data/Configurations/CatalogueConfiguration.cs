using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data.Configurations;

public sealed class SupplierConfiguration : IEntityTypeConfiguration<Supplier>
{
    public void Configure(EntityTypeBuilder<Supplier> e)
    {
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        e.Property(x => x.TradingAs).HasMaxLength(120);
        e.Property(x => x.DeliveryTerms).HasMaxLength(500);
        e.Property(x => x.Notes).HasMaxLength(1000);
        e.HasIndex(x => x.Name).IsUnique();
    }
}

public sealed class ProductLineConfiguration : IEntityTypeConfiguration<ProductLine>
{
    public void Configure(EntityTypeBuilder<ProductLine> e)
    {
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        e.Property(x => x.Description).HasMaxLength(500);

        e.HasOne(x => x.Supplier)
            .WithMany(s => s.ProductLines)
            .HasForeignKey(x => x.SupplierId)
            // Materials are retired by status and never deleted, because deletion
            // would prevent historic quotes from resolving.
            .OnDelete(DeleteBehavior.NoAction);

        e.HasOne(x => x.Brand)
            .WithMany(b => b.ProductLines)
            .HasForeignKey(x => x.BrandId)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => new { x.SupplierId, x.Name, x.ThicknessMm }).IsUnique();
    }
}

public sealed class BrandConfiguration : IEntityTypeConfiguration<Brand>
{
    public void Configure(EntityTypeBuilder<Brand> e)
    {
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        e.Property(x => x.MaterialWarranty).HasMaxLength(60);
        e.Property(x => x.WorkmanshipWarranty).HasMaxLength(60);
        e.Ignore(x => x.HasConfirmedWarranty);

        e.HasIndex(x => x.Name).IsUnique();

        // A warranty is confirmed as a pair or not at all. Half a warranty would
        // print on a quotation as if it were the whole of it.
        e.ToTable(t => t.HasCheckConstraint(
            "CK_Brand_WarrantyComplete",
            "([MaterialWarranty] IS NULL AND [WorkmanshipWarranty] IS NULL) OR " +
            "([MaterialWarranty] IS NOT NULL AND [WorkmanshipWarranty] IS NOT NULL)"));
    }
}

public sealed class SheetSizeConfiguration : IEntityTypeConfiguration<SheetSize>
{
    public void Configure(EntityTypeBuilder<SheetSize> e)
    {
        e.Ignore(x => x.AreaM2);

        e.HasOne(x => x.ProductLine)
            .WithMany(p => p.SheetSizes)
            .HasForeignKey(x => x.ProductLineId)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => new { x.ProductLineId, x.LengthMm, x.WidthMm }).IsUnique();
    }
}

public sealed class PriceBandConfiguration : IEntityTypeConfiguration<PriceBand>
{
    public void Configure(EntityTypeBuilder<PriceBand> e)
    {
        e.Property(x => x.Code).HasMaxLength(40).IsRequired();
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();

        e.HasOne(x => x.Supplier)
            .WithMany(s => s.PriceBands)
            .HasForeignKey(x => x.SupplierId)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasOne(x => x.ProductLine)
            .WithMany()
            .HasForeignKey(x => x.ProductLineId)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => new { x.ProductLineId, x.Code }).IsUnique();
    }
}

public sealed class ColourConfiguration : IEntityTypeConfiguration<Colour>
{
    public void Configure(EntityTypeBuilder<Colour> e)
    {
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        e.Property(x => x.SupplierCode).HasMaxLength(60);
        e.Property(x => x.Range).HasMaxLength(60);
        e.Ignore(x => x.IsSelectable);

        e.HasOne(x => x.ProductLine)
            .WithMany(p => p.Colours)
            .HasForeignKey(x => x.ProductLineId)
            .OnDelete(DeleteBehavior.NoAction);

        // Optional. Colours from band-priced lines reference their band; colours
        // from item-priced suppliers do not. This is the structure that
        // accommodates all five suppliers.
        e.HasOne(x => x.PriceBand)
            .WithMany(p => p.Colours)
            .HasForeignKey(x => x.PriceBandId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => new { x.ProductLineId, x.Name }).IsUnique();
        e.HasIndex(x => x.Status);
    }
}

public sealed class MaterialPriceConfiguration : IEntityTypeConfiguration<MaterialPrice>
{
    public void Configure(EntityTypeBuilder<MaterialPrice> e)
    {
        e.Property(x => x.PricePerSqm).HasPrecision(18, 4);
        e.Property(x => x.CapturedByUserId).HasMaxLength(450);

        e.HasOne(x => x.Colour)
            .WithMany()
            .HasForeignKey(x => x.ColourId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasOne(x => x.PriceBand)
            .WithMany()
            .HasForeignKey(x => x.PriceBandId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasOne(x => x.SheetSize)
            .WithMany()
            .HasForeignKey(x => x.SheetSizeId)
            .OnDelete(DeleteBehavior.NoAction);

        e.ToTable(t =>
        {
            // A price belongs to a colour or to a band, never to both and never to
            // neither. Three suppliers price by band, two by item.
            t.HasCheckConstraint(
                "CK_MaterialPrice_ColourOrBand",
                "([ColourId] IS NOT NULL AND [PriceBandId] IS NULL) OR ([ColourId] IS NULL AND [PriceBandId] IS NOT NULL)");

            // A price of zero is the failure the system exists to remove.
            t.HasCheckConstraint("CK_MaterialPrice_Positive", "[PricePerSqm] > 0");
            t.HasCheckConstraint("CK_MaterialPrice_Period", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");

            // Rejects two prices for the same key in force on the same day. Created
            // in the migration; declared here so EF Core does not use an OUTPUT
            // clause, which SQL Server refuses on a table with a trigger.
            t.HasTrigger(PriceTriggers.MaterialPriceNoOverlap);
        });

        // Supports every price resolution the system performs.
        e.HasIndex(x => new { x.ColourId, x.SheetSizeId, x.EffectiveFrom });
        e.HasIndex(x => new { x.PriceBandId, x.SheetSizeId, x.EffectiveFrom });

        // At most one open-ended price per key. The trigger covers closed periods.
        e.HasIndex(x => new { x.ColourId, x.SheetSizeId })
            .IsUnique()
            .HasFilter("[ColourId] IS NOT NULL AND [EffectiveTo] IS NULL")
            .HasDatabaseName("UX_MaterialPrices_OneOpenPricePerColour");
        e.HasIndex(x => new { x.PriceBandId, x.SheetSizeId })
            .IsUnique()
            .HasFilter("[PriceBandId] IS NOT NULL AND [EffectiveTo] IS NULL")
            .HasDatabaseName("UX_MaterialPrices_OneOpenPricePerBand");
    }
}

public sealed class RateItemConfiguration : IEntityTypeConfiguration<RateItem>
{
    public void Configure(EntityTypeBuilder<RateItem> e)
    {
        e.Property(x => x.Name).HasMaxLength(120).IsRequired();
        e.Property(x => x.Description).HasMaxLength(500);
        e.Property(x => x.DerivedFromRateItemMultiplier).HasPrecision(5, 2);
        e.Property(x => x.DerivationFactor).HasPrecision(9, 4);

        // Overtime is normal fabrication x 1.5 and installation mirrors
        // fabrication, expressed as a relationship rather than a duplicated figure.
        e.HasOne(x => x.DerivedFromRateItem)
            .WithMany()
            .HasForeignKey(x => x.DerivedFromRateItemId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.HasIndex(x => x.Name).IsUnique();

        // The rate card and the costing sheet list lines in this order.
        e.HasIndex(x => new { x.Category, x.SortOrder });
    }
}

public sealed class RatePriceConfiguration : IEntityTypeConfiguration<RatePrice>
{
    public void Configure(EntityTypeBuilder<RatePrice> e)
    {
        e.Property(x => x.Amount).HasPrecision(18, 2);

        e.HasOne(x => x.RateItem)
            .WithMany(r => r.Prices)
            .HasForeignKey(x => x.RateItemId)
            .OnDelete(DeleteBehavior.NoAction);

        // A rate may vary by supplier: adhesive is R130 from two suppliers and
        // R250 from a third. Optional because most rates are supplier-independent.
        e.HasOne(x => x.Supplier)
            .WithMany()
            .HasForeignKey(x => x.SupplierId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.NoAction);

        e.ToTable(t =>
        {
            // A rate of zero would price a line at zero. An item with no rate has
            // no RatePrice row and reports as unresolved instead.
            t.HasCheckConstraint("CK_RatePrice_Positive", "[Amount] > 0");
            t.HasCheckConstraint("CK_RatePrice_Period", "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");
            t.HasTrigger(PriceTriggers.RatePriceNoOverlap);
        });

        e.HasIndex(x => new { x.RateItemId, x.SupplierId, x.EffectiveFrom });

        // At most one open-ended rate per item and supplier. A null supplier is the
        // general rate; SQL Server treats nulls as equal in a unique index, so there
        // is also only one open general rate per item.
        e.HasIndex(x => new { x.RateItemId, x.SupplierId })
            .IsUnique()
            .HasFilter("[EffectiveTo] IS NULL")
            .HasDatabaseName("UX_RatePrices_OneOpenRate");
    }
}

/// <summary>Names of the triggers created in the AddPriceValidityRules migration.</summary>
public static class PriceTriggers
{
    public const string MaterialPriceNoOverlap = "TR_MaterialPrices_NoOverlap";
    public const string RatePriceNoOverlap = "TR_RatePrices_NoOverlap";
}
