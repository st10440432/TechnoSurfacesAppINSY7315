using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RefuseZeroPricedLinesAndRates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RatePrice_NotNegative",
                table: "RatePrices");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RatePrice_Positive",
                table: "RatePrices",
                sql: "[Amount] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CostingLine_OverridePositive",
                table: "CostingLines",
                sql: "[OverriddenUnitPrice] IS NULL OR [OverriddenUnitPrice] > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_CostingLine_PricePositive",
                table: "CostingLines",
                sql: "[ResolvedUnitPrice] > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_RatePrice_Positive",
                table: "RatePrices");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CostingLine_OverridePositive",
                table: "CostingLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_CostingLine_PricePositive",
                table: "CostingLines");

            migrationBuilder.AddCheckConstraint(
                name: "CK_RatePrice_NotNegative",
                table: "RatePrices",
                sql: "[Amount] >= 0");
        }
    }
}
