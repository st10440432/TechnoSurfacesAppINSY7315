using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBillOfQuantities : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AllowanceAmount",
                table: "QuoteVersions",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<string>(
                name: "AllowanceDescription",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "AllowancesOnOwnLines",
                table: "QuoteVersions",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "Layout",
                table: "QuoteVersions",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "Section",
                table: "QuoteItems",
                type: "nvarchar(120)",
                maxLength: 120,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Unit",
                table: "QuoteItems",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "No");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AllowanceAmount",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "AllowanceDescription",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "AllowancesOnOwnLines",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "Layout",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "Section",
                table: "QuoteItems");

            migrationBuilder.DropColumn(
                name: "Unit",
                table: "QuoteItems");
        }
    }
}
