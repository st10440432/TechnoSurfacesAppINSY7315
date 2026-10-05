using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCostingOverridesAndTransport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TransportAmount",
                table: "QuoteVersions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "CustomerReference",
                table: "Quotes",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DeliveryAddress",
                table: "Quotes",
                type: "nvarchar(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsQuantityOverridden",
                table: "CostingLines",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "OverriddenUnitPrice",
                table: "CostingLines",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TransportAmount",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "CustomerReference",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "DeliveryAddress",
                table: "Quotes");

            migrationBuilder.DropColumn(
                name: "IsQuantityOverridden",
                table: "CostingLines");

            migrationBuilder.DropColumn(
                name: "OverriddenUnitPrice",
                table: "CostingLines");
        }
    }
}
