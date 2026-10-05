using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddPriceValidityRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "UX_RatePrices_OneOpenRate",
                table: "RatePrices",
                columns: new[] { "RateItemId", "SupplierId" },
                unique: true,
                filter: "[EffectiveTo] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RatePrice_NotNegative",
                table: "RatePrices",
                sql: "[Amount] >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RatePrice_Period",
                table: "RatePrices",
                sql: "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");

            migrationBuilder.CreateIndex(
                name: "UX_MaterialPrices_OneOpenPricePerBand",
                table: "MaterialPrices",
                columns: new[] { "PriceBandId", "SheetSizeId" },
                unique: true,
                filter: "[PriceBandId] IS NOT NULL AND [EffectiveTo] IS NULL");

            migrationBuilder.CreateIndex(
                name: "UX_MaterialPrices_OneOpenPricePerColour",
                table: "MaterialPrices",
                columns: new[] { "ColourId", "SheetSizeId" },
                unique: true,
                filter: "[ColourId] IS NOT NULL AND [EffectiveTo] IS NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialPrice_Period",
                table: "MaterialPrices",
                sql: "[EffectiveTo] IS NULL OR [EffectiveTo] >= [EffectiveFrom]");

            migrationBuilder.AddCheckConstraint(
                name: "CK_MaterialPrice_Positive",
                table: "MaterialPrices",
                sql: "[PricePerSqm] > 0");

            // For one price key, exactly one price may be in force on any day. The
            // filtered unique indexes cover open-ended prices; these triggers cover
            // closed periods as well. Two prices overlap when each starts on or
            // before the day the other ends.
            //
            // Each trigger is created through EXEC because CREATE TRIGGER must be the
            // first statement in a batch, and the idempotent script the pipeline
            // generates wraps every migration in an IF block.
            migrationBuilder.Sql(@"
EXEC(N'CREATE TRIGGER dbo.TR_MaterialPrices_NoOverlap
ON dbo.MaterialPrices
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN dbo.MaterialPrices p
          ON p.Id <> i.Id
         AND p.SheetSizeId = i.SheetSizeId
         AND ((i.ColourId IS NOT NULL AND p.ColourId = i.ColourId)
           OR (i.PriceBandId IS NOT NULL AND p.PriceBandId = i.PriceBandId))
         AND p.EffectiveFrom <= ISNULL(i.EffectiveTo, ''9999-12-31'')
         AND i.EffectiveFrom <= ISNULL(p.EffectiveTo, ''9999-12-31''))
        THROW 51001, ''Two prices for the same material and sheet size would be in force on the same day. Close the current price before the new one starts.'', 1;
END');");

            migrationBuilder.Sql(@"
EXEC(N'CREATE TRIGGER dbo.TR_RatePrices_NoOverlap
ON dbo.RatePrices
AFTER INSERT, UPDATE
AS
BEGIN
    SET NOCOUNT ON;
    IF EXISTS (
        SELECT 1
        FROM inserted i
        JOIN dbo.RatePrices p
          ON p.Id <> i.Id
         AND p.RateItemId = i.RateItemId
         AND ((p.SupplierId IS NULL AND i.SupplierId IS NULL) OR p.SupplierId = i.SupplierId)
         AND p.EffectiveFrom <= ISNULL(i.EffectiveTo, ''9999-12-31'')
         AND i.EffectiveFrom <= ISNULL(p.EffectiveTo, ''9999-12-31''))
        THROW 51002, ''Two rates for the same item would be in force on the same day. Close the current rate before the new one starts.'', 1;
END');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_MaterialPrices_NoOverlap;");
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_RatePrices_NoOverlap;");

            migrationBuilder.DropIndex(
                name: "UX_RatePrices_OneOpenRate",
                table: "RatePrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RatePrice_NotNegative",
                table: "RatePrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_RatePrice_Period",
                table: "RatePrices");

            migrationBuilder.DropIndex(
                name: "UX_MaterialPrices_OneOpenPricePerBand",
                table: "MaterialPrices");

            migrationBuilder.DropIndex(
                name: "UX_MaterialPrices_OneOpenPricePerColour",
                table: "MaterialPrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialPrice_Period",
                table: "MaterialPrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_MaterialPrice_Positive",
                table: "MaterialPrices");
        }
    }
}
