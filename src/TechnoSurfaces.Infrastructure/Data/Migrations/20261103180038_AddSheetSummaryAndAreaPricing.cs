using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSheetSummaryAndAreaPricing : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "PricePerM2",
                table: "QuoteItems",
                type: "decimal(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuoteBufferStock",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteVersionId = table.Column<int>(type: "int", nullable: false),
                    Material = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Sheets = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteBufferStock", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteBufferStock_QuoteVersions_QuoteVersionId",
                        column: x => x.QuoteVersionId,
                        principalTable: "QuoteVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteBufferStock_QuoteVersionId_Material",
                table: "QuoteBufferStock",
                columns: new[] { "QuoteVersionId", "Material" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteBufferStock");

            migrationBuilder.DropColumn(
                name: "PricePerM2",
                table: "QuoteItems");
        }
    }
}
