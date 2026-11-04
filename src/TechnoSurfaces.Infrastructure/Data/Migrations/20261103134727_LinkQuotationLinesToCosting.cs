using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class LinkQuotationLinesToCosting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowSheetCounts",
                table: "QuoteVersions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "CarriesOtherCosts",
                table: "QuotationLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "CostingLineId",
                table: "QuotationLines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotationLines_CostingLineId",
                table: "QuotationLines",
                column: "CostingLineId");

            migrationBuilder.AddForeignKey(
                name: "FK_QuotationLines_CostingLines_CostingLineId",
                table: "QuotationLines",
                column: "CostingLineId",
                principalTable: "CostingLines",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_QuotationLines_CostingLines_CostingLineId",
                table: "QuotationLines");

            migrationBuilder.DropIndex(
                name: "IX_QuotationLines_CostingLineId",
                table: "QuotationLines");

            migrationBuilder.DropColumn(
                name: "ShowSheetCounts",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "CarriesOtherCosts",
                table: "QuotationLines");

            migrationBuilder.DropColumn(
                name: "CostingLineId",
                table: "QuotationLines");
        }
    }
}
