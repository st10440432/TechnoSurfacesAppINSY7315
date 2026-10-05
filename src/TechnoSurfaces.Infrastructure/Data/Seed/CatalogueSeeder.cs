using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data.Seed;

/// <summary>
/// Seeds the catalogue from the client's own supplier price lists.
///
/// This is real data, not placeholders. Three of the five suppliers price by colour
/// band and two price each item individually, so the seed exercises both strategies
/// and proves the model accommodates all five.
///
/// Sources and their dates are recorded against each supplier. The Perago and
/// Magicstone list is from June 2023 and the interface flags it as stale, which is
/// itself information the estimator needs.
///
/// The rand values in the client's January costing workbook are sample data and are
/// deliberately not used here.
/// </summary>
public static class CatalogueSeeder
{
    /// <summary>
    /// The prefix of the Staron codes an earlier version of this seeder made up. The
    /// Staron list carries no product codes, so those codes matched nothing a
    /// supplier would recognise on an order.
    /// </summary>
    private const string InventedStaronCodePrefix = "STARON-";

    /// <summary>The date of the Max on Top list the catalogue was seeded from.</summary>
    private static readonly DateOnly MaxOnTopListDate = new(2026, 8, 3);

    public static async Task SeedAsync(TechnoSurfacesDbContext db, CancellationToken ct = default)
    {
        await ClearInventedStaronCodesAsync(db, ct);
        await CorrectMaxOnTopPricesAsync(db, ct);

        if (await db.Suppliers.AnyAsync(ct)) return;

        await SeedSurfaceStudioAsync(db, ct);
        await SeedStaronAsync(db, ct);
        await SeedMaxOnTopAsync(db, ct);
        await SeedWoodcentreAsync(db, ct);
        await SeedPeragoAsync(db, ct);
    }

    /// <summary>
    /// A database seeded before this change holds the made-up Staron codes. They are
    /// cleared on start-up, through EF Core so the audit trail records the change.
    /// Only Staron colours whose code still has the made-up form are touched; a code
    /// the Managing Director has since entered is left alone.
    /// </summary>
    private static async Task ClearInventedStaronCodesAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var invented = await db.Colours
            .Where(c => c.ProductLine!.Supplier!.Name == "Staron (Salvocorp)"
                     && c.SupplierCode.StartsWith(InventedStaronCodePrefix))
            .ToListAsync(ct);

        if (invented.Count == 0) return;

        foreach (var colour in invented)
            colour.SupplierCode = "";

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// An earlier version of this seeder took Max on Top's price per sheet, which the
    /// list shows rounded to the rand, and worked back to a price per square metre a
    /// few cents off the list's own figure. This puts the list figure back on a
    /// database seeded before. Only a price still dated from the list and within a
    /// rand of its figure is changed; a price the Managing Director has entered since
    /// is left alone. The change goes through EF Core, so the audit trail records it.
    /// </summary>
    private static async Task CorrectMaxOnTopPricesAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var listed = MaxOnTopPricesPerSqm();
        var prices = await db.MaterialPrices
            .Include(p => p.Colour)
            .Where(p => p.Colour != null
                     && p.Colour.ProductLine!.Supplier!.Name == "Max on Top"
                     && p.EffectiveFrom == MaxOnTopListDate)
            .ToListAsync(ct);

        var corrected = 0;
        foreach (var price in prices)
        {
            if (!listed.TryGetValue(price.Colour!.SupplierCode, out var perSqm)) continue;
            var difference = Math.Abs(price.PricePerSqm - perSqm);
            if (difference == 0m || difference >= 1m) continue;

            price.PricePerSqm = perSqm;
            corrected++;
        }

        if (corrected > 0)
            await db.SaveChangesAsync(ct);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<Supplier> AddSupplierAsync(
        TechnoSurfacesDbContext db, string name, string? tradingAs, PricingStructure structure,
        DateOnly listDated, decimal adhesive, string deliveryTerms, CancellationToken ct)
    {
        var s = new Supplier
        {
            Name = name,
            TradingAs = tradingAs,
            PricingStructure = structure,
            PriceListDated = listDated,
            AdhesivePrice = adhesive,
            DeliveryTerms = deliveryTerms
        };
        db.Suppliers.Add(s);
        await db.SaveChangesAsync(ct);
        return s;
    }

    private static async Task<ProductLine> AddLineAsync(
        TechnoSurfacesDbContext db, Supplier supplier, string name, int thicknessMm, CancellationToken ct)
    {
        var p = new ProductLine { SupplierId = supplier.Id, Name = name, ThicknessMm = thicknessMm };
        db.ProductLines.Add(p);
        await db.SaveChangesAsync(ct);
        return p;
    }

    private static async Task<SheetSize> AddSizeAsync(
        TechnoSurfacesDbContext db, ProductLine line, int lengthMm, int widthMm, CancellationToken ct)
    {
        var z = new SheetSize { ProductLineId = line.Id, LengthMm = lengthMm, WidthMm = widthMm };
        db.SheetSizes.Add(z);
        await db.SaveChangesAsync(ct);
        return z;
    }

    /// <summary>Adds a price published per square metre, as the band-priced suppliers do.</summary>
    private static void AddBandPrice(
        TechnoSurfacesDbContext db, PriceBand band, SheetSize size, decimal perSqm, DateOnly from)
        => db.MaterialPrices.Add(new MaterialPrice
        {
            PriceBandId = band.Id,
            SheetSizeId = size.Id,
            PricePerSqm = perSqm,
            EffectiveFrom = from,
            CapturedAtUtc = DateTime.UtcNow
        });

    /// <summary>
    /// Adds a price published per sheet, as the item-priced suppliers do. The
    /// per-square-metre figure is derived so that it rounds back to the published
    /// sheet price to the cent.
    /// </summary>
    private static void AddItemPrice(
        TechnoSurfacesDbContext db, Colour colour, SheetSize size, decimal sheetPrice, DateOnly from)
        => db.MaterialPrices.Add(new MaterialPrice
        {
            ColourId = colour.Id,
            SheetSizeId = size.Id,
            PricePerSqm = MaterialPrice.PerSqmFromSheetPrice(sheetPrice, size),
            EffectiveFrom = from,
            CapturedAtUtc = DateTime.UtcNow
        });

    /// <summary>
    /// Adds a colour's price as the supplier publishes it per square metre. Max on Top
    /// lists a whole-rand price per square metre; its price per sheet is that times the
    /// sheet's area, so it is the per-square-metre figure that is stored.
    /// </summary>
    private static void AddColourPricePerSqm(
        TechnoSurfacesDbContext db, Colour colour, SheetSize size, decimal perSqm, DateOnly from)
        => db.MaterialPrices.Add(new MaterialPrice
        {
            ColourId = colour.Id,
            SheetSizeId = size.Id,
            PricePerSqm = perSqm,
            EffectiveFrom = from,
            CapturedAtUtc = DateTime.UtcNow
        });

    // -------------------------------------------- 1. Surface Studio (by band)

    private static async Task SeedSurfaceStudioAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var dated = new DateOnly(2026, 3, 1);
        var supplier = await AddSupplierAsync(db, "Surface Studio", null, PricingStructure.Band, dated,
            adhesive: 130.00m, "R550 for orders under 10 sheets; outside Gauteng quoted per order.", ct);

        // Full acrylic, 12mm and 6mm. WHITE is band A1 at 6mm but A2 at 12mm, which
        // is why thickness sits on the product line.
        var fullAcrylic12 = await AddLineAsync(db, supplier, "Infinito Full Acrylic", 12, ct);
        var fullAcrylic6 = await AddLineAsync(db, supplier, "Infinito Full Acrylic", 6, ct);
        var modified12 = await AddLineAsync(db, supplier, "Infinito Modified", 12, ct);

        var size12 = await AddSizeAsync(db, fullAcrylic12, 3680, 760, ct);
        var size6 = await AddSizeAsync(db, fullAcrylic6, 3050, 760, ct);
        var sizeMod = await AddSizeAsync(db, modified12, 3680, 760, ct);

        var bandsA = new (string Code, decimal PerSqm)[]
        {
            ("A1", 1200.00m), ("A2", 1540.00m), ("A3", 1800.00m), ("A4", 2255.00m)
        };
        var bandsM = new (string Code, decimal PerSqm)[]
        {
            ("M1", 1280.00m), ("M2", 1530.00m), ("M3", 1830.00m), ("M4", 1980.00m)
        };

        var aBands = new Dictionary<string, PriceBand>();
        foreach (var (code, perSqm) in bandsA)
        {
            var band = new PriceBand { SupplierId = supplier.Id, ProductLineId = fullAcrylic12.Id, Code = code, Name = $"Group {code}" };
            db.PriceBands.Add(band);
            await db.SaveChangesAsync(ct);
            AddBandPrice(db, band, size12, perSqm, dated);
            aBands[code] = band;
        }

        // Group A1 is the 6mm sheet: R2 781,60 divided by R1 200,00 is 2,318 square
        // metres, which is 3050 x 760.
        AddBandPrice(db, aBands["A1"], size6, 1200.00m, dated);

        var mBands = new Dictionary<string, PriceBand>();
        foreach (var (code, perSqm) in bandsM)
        {
            var band = new PriceBand { SupplierId = supplier.Id, ProductLineId = modified12.Id, Code = code, Name = $"Group {code}" };
            db.PriceBands.Add(band);
            await db.SaveChangesAsync(ct);
            AddBandPrice(db, band, sizeMod, perSqm, dated);
            mBands[code] = band;
        }
        await db.SaveChangesAsync(ct);

        var fullColours = new (string Code, string Name, string Band)[]
        {
            ("SS-A-INF-368", "ASPEN CORAL", "A3"), ("SS-A-INF-101", "BLACK NIGHT", "A3"),
            ("SS-A-INF-3403", "BOSTON", "A4"), ("SS-A-INF-1016", "CLOUDY CALACATTA", "A4"),
            ("SS-A-INF-028", "CONCRETE GREY", "A3"), ("SS-A-INF-105", "CREAM", "A3"),
            ("SS-A-INF-415", "DOVE", "A3"), ("SS-A-INF-1009", "LATTE", "A4"),
            ("SS-A-INF-331", "LIMISTONE", "A3"), ("SS-A-INF-309", "MILK WHITE", "A3"),
            ("SS-A-INF-1021", "NUTELLA", "A4"), ("SS-A-INF-002", "PURE BLACK", "A3"),
            ("SS-A-INF-0005", "VANILLA", "A4"), ("SS-A-INF-003", "WHITE", "A2")
        };
        foreach (var (code, name, band) in fullColours)
            db.Colours.Add(new Colour { ProductLineId = fullAcrylic12.Id, PriceBandId = aBands[band].Id, Name = name, SupplierCode = code });

        db.Colours.Add(new Colour { ProductLineId = fullAcrylic6.Id, PriceBandId = aBands["A1"].Id, Name = "WHITE", SupplierCode = "SS-A-INF-003" });

        var modColours = new (string Code, string Name, string Band)[]
        {
            ("SS-M-INF-003", "WHITE", "M1"), ("SS-M-INF-368", "ASPEN CORAL", "M2"),
            ("SS-M-INF-178", "ASPEN SAND", "M2"), ("SS-M-INF-005", "ASTERIX", "M3"),
            ("SS-M-INF-3402", "ATLANTA", "M3"), ("SS-M-INF-3516", "BLACK CALACATTA", "M4"),
            ("SS-M-INF-101", "BLACK NIGHT", "M2"), ("SS-M-INF-3401", "CHICAGO", "M3"),
            ("SS-M-INF-028", "CONCRETE GREY", "M2"), ("SS-M-INF-105", "CREAM", "M2"),
            ("SS-M-INF-323", "CRYSTAL BEIGE", "M2"), ("SS-M-INF-1295", "DAYLIGHT", "M2"),
            ("SS-M-INF-2502", "DREAMY WHITE", "M3"), ("SS-M-INF-307", "FROST", "M2"),
            ("SS-M-INF-002", "IVORY", "M2"), ("SS-M-INF-3509", "LIGHT CALACATTA", "M4"),
            ("SS-M-INF-S006", "LIGHT GREY", "M2"), ("SS-M-INF-331", "LIMISTONE", "M2"),
            ("SS-M-INF-309", "MILK WHITE", "M2"), ("SS-M-INF-1296", "MINT", "M2"),
            ("SS-M-INF-007", "NIGHT GLEAM", "M2"), ("SS-M-INF-1265", "RED", "M2"),
            ("SS-M-INF-C002", "RIMY CONCRETE", "M2"), ("SS-M-INF-535", "WHITE QUARTZ", "M2")
        };
        foreach (var (code, name, band) in modColours)
            db.Colours.Add(new Colour { ProductLineId = modified12.Id, PriceBandId = mBands[band].Id, Name = name, SupplierCode = code });

        await db.SaveChangesAsync(ct);
    }

    // --------------------------------------- 2. Staron via Salvocorp (by band)

    private static async Task SeedStaronAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var dated = new DateOnly(2025, 3, 1);

        // The Staron list is on Salvocorp letterhead carrying the same telephone
        // numbers as the Perago and Magicstone list, so supplier and brand are
        // modelled separately.
        var supplier = await AddSupplierAsync(db, "Staron (Salvocorp)", "Salvocorp Pty Ltd", PricingStructure.Band, dated,
            adhesive: 130.00m, "Quoted per order.", ct);

        var staron12 = await AddLineAsync(db, supplier, "Staron", 12, ct);
        var staron6 = await AddLineAsync(db, supplier, "Staron", 6, ct);

        var size12 = await AddSizeAsync(db, staron12, 3680, 760, ct);
        var size6 = await AddSizeAsync(db, staron6, 2500, 760, ct);

        var categories12 = new (string Name, decimal PerSqm)[]
        {
            ("Bright White", 1550.00m), ("Solid", 1683.00m), ("Sanded", 1800.00m),
            ("Aspen", 1880.00m), ("Pebble", 1975.00m), ("Metallic", 2150.00m),
            ("Terrazzo", 2353.00m), ("Quarry", 2353.00m), ("Tempest", 2663.00m),
            ("Supreme", 2760.00m), ("Premier range", 2850.00m)
        };

        foreach (var (name, perSqm) in categories12)
        {
            var band = new PriceBand { SupplierId = supplier.Id, ProductLineId = staron12.Id, Code = name, Name = name };
            db.PriceBands.Add(band);
            await db.SaveChangesAsync(ct);
            AddBandPrice(db, band, size12, perSqm, dated);

            // Staron's many colour names map into these categories. One
            // representative colour per category is seeded; the Managing Director
            // adds the rest through the catalogue screen. The Staron list gives no
            // product codes, so none is recorded rather than one being made up.
            db.Colours.Add(new Colour
            {
                ProductLineId = staron12.Id,
                PriceBandId = band.Id,
                Name = name
            });
        }

        var bw6 = new PriceBand { SupplierId = supplier.Id, ProductLineId = staron6.Id, Code = "Bright White", Name = "Bright White" };
        db.PriceBands.Add(bw6);
        await db.SaveChangesAsync(ct);
        AddBandPrice(db, bw6, size6, 1485.00m, dated);
        db.Colours.Add(new Colour { ProductLineId = staron6.Id, PriceBandId = bw6.Id, Name = "Bright White" });

        await db.SaveChangesAsync(ct);
    }

    // -------------------------------------------------- 3. Max on Top (by item)

    /// <summary>
    /// The Max on Top list of 3 August 2026: code, colour, product line, sheet size and
    /// the price per square metre as the list gives it. The price per sheet on the list
    /// is this times the sheet's area.
    /// </summary>
    private static readonly (string Code, string Name, string Line, string Size, decimal PerSqm)[] MaxOnTopList =
    {
        ("GAVONITE4312472", "Alaskan Stone 4312", "pure12", "p12_3658x760", 2983m),
        ("WSOLIDSURFACE8206", "Alpine Shimmer 8206", "pure12", "p12_3680x760", 2983m),
        ("WSOLIDSURFACE9015", "Arctica 9015", "pure12", "p12_3680x760", 1670m),
        ("WSOLIDSURFACE90156", "Arctica 9015 6mm", "pure6", "p6_2440x1220", 1239m),
        ("WSOLIDSURFACE5230", "Aspen 5230", "pure12", "p12_3680x760", 1670m),
        ("WWSOLIDSURF5230920", "Aspen 5230 920", "pure12", "p12_3680x920", 1670m),
        ("WSOLIDSURFACE7502", "Avalanche 7502", "pure12", "p12_3680x760", 2075m),
        ("WSOLIDSURFACE8010", "Bone 8010", "pure12", "p12_3680x760", 1670m),
        ("GAVONITE7830472", "Bronze 7830", "pure12", "p12_3658x760", 2983m),
        ("GAVONITE8106472", "Cameo White 8106", "pure12", "p12_3680x760", 1670m),
        ("WSOLIDSURFACE9137", "Casablanca 9137", "pure12", "p12_3680x760", 1750m),
        ("GAVONITE8292472", "Cloud 8292", "pure12", "p12_3680x760", 1750m),
        ("WSOLIDSURFACE8240", "Eclipse 8240", "pure12", "p12_3680x760", 1800m),
        ("WSOLIDSURFACE8248", "Fuego 8248", "pure12", "p12_3680x760", 1670m),
        ("WSOLIDSURFACE80167", "Glacier White 8016", "pure12", "p12_3680x760", 1427m),
        ("WSOLIDSURFACE8016", "Glacier White 8016 920", "pure12", "p12_3680x920", 1427m),
        ("WSOLIDSURFACE80166", "Glacier White 8016 6mm", "pure6", "p6_2440x920", 1019m),
        ("WSOLIDSURFACE7849", "Industrial 7849", "pure12", "p12_3680x760", 2075m),
        ("GAVONITE7711472", "Jurassic 7711", "pure12", "p12_3658x760", 2983m),
        ("WSOLIDSURFACE9117", "Kokoura 9117", "pure12", "p12_3680x760", 2075m),
        ("GAVONITE7810472", "Malt 7810", "pure12", "p12_3680x760", 2385m),
        ("WSOLIDSURFACE8268", "Mango 8268", "pure12", "p12_3680x760", 1800m),
        ("WSOLIDSURFACE7842", "New Concrete 7842", "pure12", "p12_3680x760", 2075m),
        ("GAVONITE8090472", "Snowfall 8090", "pure12", "p12_3680x760", 2983m),
        ("WSOLIDSURFACE7820", "Starshine 7820", "pure12", "p12_3680x760", 2075m),

        ("WSOLIDSURFACE2501", "Simply Altitude 2501", "modified12", "m12_3680x760", 1950m),
        ("WSOLIDSURFACE2028", "Simply Arctica 2028", "modified12", "m12_3680x760", 1450m),
        ("WSOLIDSURFACELM106", "Simply Dusk LM106", "modified12", "m12_3680x760", 1550m),
        ("GAVONITERMS8968", "Simply Grey Terrazzo 8968", "modified12", "m12_3680x760", 2196m),
        ("WSOLIDSURFACE8961", "Simply Light Cement 8961", "modified12", "m12_3680x760", 1550m),
        ("WSOLIDSURFACE8969", "Simply Marbled Grey 8969", "modified12", "m12_3680x760", 2542m),
        ("WSOLIDSURFACE3522", "Simply Marble Whisp 3522", "modified12", "m12_3680x760", 2542m),
        ("WSOLIDSURFACE8904", "Simply Morning Mist 8904", "modified12", "m12_3680x760", 1950m),
        ("WSOLIDSURFACE8967", "Simply Speckled Creme 8967", "modified12", "m12_3680x760", 1450m),
        ("WSOLIDSURFACE8905", "Simply Summit 8905", "modified12", "m12_3680x920", 2542m),
        ("WSOLIDSURFACE8960", "Simply White 8960", "modified12", "m12_3680x760", 1037m),

        ("FG25207W2630W42", "GetaCore Snowdrift GC2252", "getacore3", "g3_2040x1250", 1929m),
        ("FGC4107W2630W42", "GetaCore Dusk GC4143", "getacore3", "g3_2040x1250", 1929m),
        ("FG01107W2630W42", "GetaCore Glacier White GC2011", "getacore3", "g3_2040x1250", 1530m),
        ("FT24407W2630W42", "GetaCore Terrazzo Pebble CGT244", "getacore3", "g3_2040x1250", 2279m)
    };

    private static Dictionary<string, decimal> MaxOnTopPricesPerSqm() =>
        MaxOnTopList.ToDictionary(r => r.Code, r => r.PerSqm);

    private static async Task SeedMaxOnTopAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var dated = MaxOnTopListDate;
        var supplier = await AddSupplierAsync(db, "Max on Top", null, PricingStructure.Item, dated,
            adhesive: 130.00m, "Orders under 5 sheets incur R1 050 ex VAT.", ct);

        var pure12 = await AddLineAsync(db, supplier, "Max Pure Solid Surface", 12, ct);
        var pure6 = await AddLineAsync(db, supplier, "Max Pure Solid Surface", 6, ct);
        var modified12 = await AddLineAsync(db, supplier, "Max Modified Acrylic", 12, ct);
        var getacore3 = await AddLineAsync(db, supplier, "Max Getacore", 3, ct);

        var p12_3680x760 = await AddSizeAsync(db, pure12, 3680, 760, ct);
        var p12_3658x760 = await AddSizeAsync(db, pure12, 3658, 760, ct);
        var p12_3680x920 = await AddSizeAsync(db, pure12, 3680, 920, ct);
        var p6_2440x1220 = await AddSizeAsync(db, pure6, 2440, 1220, ct);
        var p6_2440x920 = await AddSizeAsync(db, pure6, 2440, 920, ct);
        var m12_3680x760 = await AddSizeAsync(db, modified12, 3680, 760, ct);
        var m12_3680x920 = await AddSizeAsync(db, modified12, 3680, 920, ct);
        var g3_2040x1250 = await AddSizeAsync(db, getacore3, 2040, 1250, ct);

        var lines = new Dictionary<string, ProductLine>
        {
            ["pure12"] = pure12, ["pure6"] = pure6, ["modified12"] = modified12, ["getacore3"] = getacore3
        };
        var sizes = new Dictionary<string, SheetSize>
        {
            ["p12_3680x760"] = p12_3680x760, ["p12_3658x760"] = p12_3658x760, ["p12_3680x920"] = p12_3680x920,
            ["p6_2440x1220"] = p6_2440x1220, ["p6_2440x920"] = p6_2440x920,
            ["m12_3680x760"] = m12_3680x760, ["m12_3680x920"] = m12_3680x920, ["g3_2040x1250"] = g3_2040x1250
        };

        foreach (var (code, name, line, size, perSqm) in MaxOnTopList)
        {
            var colour = new Colour { ProductLineId = lines[line].Id, Name = name, SupplierCode = code };
            db.Colours.Add(colour);
            await db.SaveChangesAsync(ct);
            AddColourPricePerSqm(db, colour, sizes[size], perSqm, dated);
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------- 4. Woodcentre (by item)

    private static async Task SeedWoodcentreAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var dated = new DateOnly(2026, 7, 1);
        var supplier = await AddSupplierAsync(db, "Woodcentre CPT", null, PricingStructure.Item, dated,
            adhesive: 250.00m, "Buy-out stock priced on application.", ct);

        var surwell = await AddLineAsync(db, supplier, "Surwell Modified Acrylic", 12, ct);
        var schemar = await AddLineAsync(db, supplier, "SchemaR Full Acrylic", 12, ct);

        var surwellSize = await AddSizeAsync(db, surwell, 3680, 760, ct);
        var schemarSize = await AddSizeAsync(db, schemar, 3680, 760, ct);

        var surwellRows = new (string Code, string Name, string? Range, decimal Sheet)[]
        {
            ("3202217", "SKY WHITE", null, 2950m), ("3202212", "ALMOND", "SOLID", 3500m),
            ("3202203", "BLACK BALL", "SOLID", 3500m), ("3202211", "SLATE", "SOLID", 3500m),
            ("3202210", "STEEL", "SOLID", 3500m), ("3202202", "REES RED", "SOLID", 3500m),
            ("3202204", "NEBULA BALL", "NEBULA", 3800m), ("3202205", "NEBULA MUDDY", "NEBULA", 3800m),
            ("3202213", "POPLAR ROCKY", "POPLAR", 4000m), ("3202206", "STELLA BIRCH", "STELLA", 4500m),
            ("3202207", "STELLA DOVE", "STELLA", 4500m), ("3202208", "STELLA FAME", "STELLA", 4500m),
            ("3202209", "MET BLACK", "MET", 5500m), ("3202214", "MET GREY", "MET", 5500m)
        };
        foreach (var (code, name, range, sheet) in surwellRows)
        {
            var colour = new Colour { ProductLineId = surwell.Id, Name = name, SupplierCode = code, Range = range };
            db.Colours.Add(colour);
            await db.SaveChangesAsync(ct);
            AddItemPrice(db, colour, surwellSize, sheet, dated);
        }

        var schemarRows = new (string Code, string Name, decimal Sheet)[]
        {
            ("3202032", "Black Pitch", 5600m), ("3202020", "Galaxy Ball", 5800m),
            ("3202023", "Galaxy Muddy", 4850m), ("3202039", "Iron", 4850m),
            ("3202037", "Millet Fantasia", 5000m), ("3202034", "Millet Rocky", 5100m),
            ("3202036", "Millet Smokestone", 3800m), ("3202053", "Moire Shade Concrete", 8650m),
            ("3202052", "Moire White Onyx", 8650m), ("3202047", "Pearl Grey", 7600m),
            ("3202027", "Shingle Fame", 5400m), ("3202035", "Shingle Birch", 5400m),
            ("3202031", "Soft White", 3900m), ("3202021", "Zebra Stone", 5250m)
        };
        foreach (var (code, name, sheet) in schemarRows)
        {
            var colour = new Colour { ProductLineId = schemar.Id, Name = name, SupplierCode = code };
            db.Colours.Add(colour);
            await db.SaveChangesAsync(ct);
            AddItemPrice(db, colour, schemarSize, sheet, dated);
        }

        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------ 5. Perago and Magicstone (by item)

    private static async Task SeedPeragoAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        // The oldest list in use. The interface flags a supplier whose list is over
        // a year old, because staleness is itself a business risk.
        var dated = new DateOnly(2023, 6, 1);
        var supplier = await AddSupplierAsync(db, "Perago and Magicstone", "Salvocorp Pty Ltd", PricingStructure.Item, dated,
            adhesive: 130.00m, "Johannesburg metro R510; Pretoria metro R650; Western Cape R510 within 40km, R950 beyond.", ct);

        var perago12 = await AddLineAsync(db, supplier, "Perago 100% Acrylic", 12, ct);
        var perago6 = await AddLineAsync(db, supplier, "Perago 100% Acrylic", 6, ct);
        var magicstone12 = await AddLineAsync(db, supplier, "Magicstone Modified", 12, ct);

        var p12_760 = await AddSizeAsync(db, perago12, 3660, 760, ct);
        var p12_900 = await AddSizeAsync(db, perago12, 3660, 900, ct);
        var p6_760 = await AddSizeAsync(db, perago6, 3660, 760, ct);
        var m12_760 = await AddSizeAsync(db, magicstone12, 3660, 760, ct);

        // Perago publish both figures, so the per-square-metre price is used
        // directly and the sheet price derives from it. Verified: R1 520,00 over
        // 2,7816 square metres gives R4 228,03, which is the published figure.
        var peragoRows = new (string Name, SheetSize Size, decimal PerSqm)[]
        {
            ("Perago Classic White", p12_760, 1520.00m),
            ("Classic White 900", p12_900, 2140.00m),
            ("Classic White 6mm", p6_760, 1295.00m)
        };
        foreach (var (name, size, perSqm) in peragoRows)
        {
            var line = size.ProductLineId == perago6.Id ? perago6 : perago12;
            var colour = new Colour { ProductLineId = line.Id, Name = name, SupplierCode = name };
            db.Colours.Add(colour);
            await db.SaveChangesAsync(ct);
            db.MaterialPrices.Add(new MaterialPrice
            {
                ColourId = colour.Id,
                SheetSizeId = size.Id,
                PricePerSqm = perSqm,
                EffectiveFrom = dated,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        var magicstoneRows = new (string Name, decimal PerSqm)[]
        {
            ("Magicstone Snow White", 1450.00m), ("Magicstone Moss", 1595.00m),
            ("Magicstone Sand Stone", 1595.00m), ("Magicstone White Jasper", 1595.00m),
            ("Magicstone Bamboo", 1595.00m), ("Magicstone Stardust", 2085.00m),
            ("Magicstone Moonstone", 2085.00m), ("Magicstone Opaline", 2085.00m),
            ("Magicstone Amazon", 2085.00m), ("Magicstone Windswept", 2085.00m),
            ("Magicstone Thunder", 2085.00m), ("Magicstone Gypsum", 2085.00m),
            ("Magicstone Grey Pearl", 2100.00m)
        };
        foreach (var (name, perSqm) in magicstoneRows)
        {
            var colour = new Colour { ProductLineId = magicstone12.Id, Name = name, SupplierCode = name };
            db.Colours.Add(colour);
            await db.SaveChangesAsync(ct);
            db.MaterialPrices.Add(new MaterialPrice
            {
                ColourId = colour.Id,
                SheetSizeId = m12_760.Id,
                PricePerSqm = perSqm,
                EffectiveFrom = dated,
                CapturedAtUtc = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync(ct);
    }
}
