using Microsoft.EntityFrameworkCore;
using TechnoSurfaces.Domain;
using TechnoSurfaces.Domain.Catalogue;

namespace TechnoSurfaces.Infrastructure.Data.Seed;

/// <summary>
/// Seeds the rate card: the chargeable lines that are not material.
///
/// The lines, their grouping and their order follow the client's costing sheet
/// (Costing January 2026.xlsx), which is also the structure the Task 1 prototype
/// rate card was built on. The workbook repeats the rate card on twelve material
/// sheets and the copies disagree in places: fabrication is R250 / R270 on the
/// Perago12mm sheet and R265 / R285 on the rest, and the seamkit is R130, R185 or
/// R220 depending on the sheet. Where the copies differ the highest figure is
/// seeded (team decision, 2 October 2026), and the Managing Director can change
/// any rate on the rate card screen.
///
/// Overtime and installation are not seeded as figures. The workbook calculates
/// them (overtime is the normal rate x 1.5, installation equals fabrication with
/// no backsplash), so they are held here as relationships to the fabrication rates.
///
/// Nine lines carry no price on any of the twelve sheets: the three sinks and
/// hardware lines, and the six wood boards the workbook leaves blank or lists under
/// "Check pricing:". They are seeded without a price, so the system reports them as
/// unresolved until the Managing Director sets a rate, rather than pricing them at a
/// figure nobody supplied.
///
/// Transport is not on the rate card. It is the "Petrol / delivery" amount the
/// Managing Director types per job, held on the quote version.
/// </summary>
public static class RateCardSeeder
{
    /// <summary>The date of the client's costing workbook the rates are taken from.</summary>
    public static readonly DateOnly RatesEffectiveFrom = new(2026, 1, 1);

    /// <summary>
    /// Rate items with no price in the client's workbook. Listed here so the gap is
    /// visible rather than buried in the data.
    /// </summary>
    public static readonly string[] AwaitingClientRates =
    {
        "MFC White Std", "Chipboard 32mm Bison", "16mm White MDF", "5mm plywood bend",
        "Marine Ply 9mm", "Marine Ply 18mm", "Sink / vanity", "Brackets", "Hardware"
    };

    public static async Task SeedAsync(TechnoSurfacesDbContext db, CancellationToken ct = default)
    {
        await RemoveUnorderedRateCardAsync(db, ct);

        if (await db.RateItems.AnyAsync(ct)) return;

        var order = 0;

        RateItem Add(string name, RateCategory category, ChargeUnit unit, decimal? amount,
            string? description = null, DerivationRule derivation = DerivationRule.Entered,
            decimal derivationFactor = 1m, bool belowTheLine = false)
        {
            var item = new RateItem
            {
                Name = name,
                Description = description,
                Category = category,
                Unit = unit,
                Derivation = derivation,
                DerivationFactor = derivationFactor,
                IsBelowTheLine = belowTheLine,
                SortOrder = ++order
            };

            if (amount is not null)
                item.Prices.Add(new RatePrice { Amount = amount.Value, EffectiveFrom = RatesEffectiveFrom });

            db.RateItems.Add(item);
            return item;
        }

        // ---- Fabrication ----

        var noBacksplash = Add("Fabrication — no backsplash, normal", RateCategory.Fabrication, ChargeUnit.Hour, 265.00m);
        var withBacksplash = Add("Fabrication — with backsplash, normal", RateCategory.Fabrication, ChargeUnit.Hour, 285.00m);
        var noBacksplashOvertime = Add("Fabrication — no backsplash, overtime", RateCategory.Fabrication, ChargeUnit.Hour, null);
        var withBacksplashOvertime = Add("Fabrication — with backsplash, overtime", RateCategory.Fabrication, ChargeUnit.Hour, null);
        Add("Thermoforming", RateCategory.Fabrication, ChargeUnit.Each, 400.00m);
        Add("Vacuum press", RateCategory.Fabrication, ChargeUnit.Each, 400.00m);
        Add("Sanding time", RateCategory.Fabrication, ChargeUnit.Hour, 100.00m);

        // ---- Consumables ----

        Add("Seamkit", RateCategory.Consumables, ChargeUnit.Each, 220.00m);
        Add("Sandpaper & consumables", RateCategory.Consumables, ChargeUnit.SquareMetre, 55.00m,
            "Quantity is the total square metres across all material lines.",
            DerivationRule.FromTotalAreaM2);
        Add("Silicon + sealing", RateCategory.Consumables, ChargeUnit.Each, 55.00m,
            "Two per sheet across all material lines. The estimator can type over it.",
            DerivationRule.FromSheetCount, derivationFactor: 2m);
        Add("Genkem", RateCategory.Consumables, ChargeUnit.Each, 140.00m);

        // ---- Installation ----

        var installation = Add("Installation — normal", RateCategory.Installation, ChargeUnit.Hour, null);
        var installationOvertime = Add("Installation — overtime", RateCategory.Installation, ChargeUnit.Hour, null);

        // ---- Wood and substrate, per sheet ----

        const string Large = "Sheet 2,75 x 1,83 m";
        const string Small = "Sheet 2,44 x 1,22 m";

        Add("MDF Bison 16mm white face", RateCategory.Wood, ChargeUnit.Sheet, 1027.20m, Large);
        Add("MDF Bison 16mm", RateCategory.Wood, ChargeUnit.Sheet, 738.30m, Large);
        Add("MDF Bison 12mm", RateCategory.Wood, ChargeUnit.Sheet, 726.53m, Large);
        Add("MDF Bison 9mm", RateCategory.Wood, ChargeUnit.Sheet, 583.15m, Large);
        Add("Chipboard 16mm", RateCategory.Wood, ChargeUnit.Sheet, 512.53m, Large);
        Add("MFC White Std", RateCategory.Wood, ChargeUnit.Sheet, null, Large);
        Add("Hardboard std 3.2", RateCategory.Wood, ChargeUnit.Sheet, 134.00m, Small);
        Add("Plywood Pine 18mm", RateCategory.Wood, ChargeUnit.Sheet, 559.00m, Small);
        Add("Chipboard 32mm Bison", RateCategory.Wood, ChargeUnit.Sheet, null, Large);
        Add("16mm White MDF", RateCategory.Wood, ChargeUnit.Sheet, null, Large);
        Add("5mm plywood bend", RateCategory.Wood, ChargeUnit.Sheet, null, Small);
        Add("Marine Ply 9mm", RateCategory.Wood, ChargeUnit.Sheet, null, Small);
        Add("Marine Ply 18mm", RateCategory.Wood, ChargeUnit.Sheet, null, Small);

        // ---- Sinks, vanities and hardware ----

        Add("Sink / vanity", RateCategory.SinksAndHardware, ChargeUnit.Each, null);
        Add("Brackets", RateCategory.SinksAndHardware, ChargeUnit.Each, null);
        Add("Hardware", RateCategory.SinksAndHardware, ChargeUnit.Each, null);

        // ---- Below the line: cost recovery, not marked up ----

        Add("Drainer grooves", RateCategory.Extras, ChargeUnit.Each, 175.00m, belowTheLine: true);
        Add("Sink / vanity cut out", RateCategory.Extras, ChargeUnit.Each, 225.00m, belowTheLine: true);
        Add("Underslung sink / vanity", RateCategory.Extras, ChargeUnit.Each, 375.00m, belowTheLine: true);
        Add("Hob cut out", RateCategory.Extras, ChargeUnit.Each, 350.00m, belowTheLine: true);

        await db.SaveChangesAsync(ct);

        // The relationships need the keys of the rates they point at, so they are
        // set once those rows exist. Installation overtime mirrors the no-backsplash
        // overtime rate, which is itself the normal rate x 1.5, as in the workbook.
        DeriveFrom(noBacksplashOvertime, noBacksplash, 1.5m);
        DeriveFrom(withBacksplashOvertime, withBacksplash, 1.5m);
        DeriveFrom(installation, noBacksplash, 1.0m);
        DeriveFrom(installationOvertime, noBacksplashOvertime, 1.0m);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Databases created before the rate card followed the workbook hold an earlier
    /// twelve-item card with no sort order. It is replaced, but only while no quote
    /// line refers to it: once a quote uses a rate item, the card is the Managing
    /// Director's to maintain and is never removed by a seed.
    /// </summary>
    private static async Task RemoveUnorderedRateCardAsync(TechnoSurfacesDbContext db, CancellationToken ct)
    {
        var hasItems = await db.RateItems.AnyAsync(ct);
        var isUnordered = hasItems && !await db.RateItems.AnyAsync(r => r.SortOrder > 0, ct);
        if (!isUnordered || await db.CostingLines.AnyAsync(l => l.RateItemId != null, ct))
            return;

        var items = await db.RateItems.ToListAsync(ct);
        foreach (var item in items)
            item.DerivedFromRateItemId = null;
        await db.SaveChangesAsync(ct);

        db.RatePrices.RemoveRange(await db.RatePrices.ToListAsync(ct));
        db.RateItems.RemoveRange(items);
        await db.SaveChangesAsync(ct);
    }

    private static void DeriveFrom(RateItem item, RateItem source, decimal multiplier)
    {
        item.DerivedFromRateItemId = source.Id;
        item.DerivedFromRateItemMultiplier = multiplier;
    }
}
