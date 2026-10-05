using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordTermsOnQuoteVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "QuoteVersionTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteVersionId = table.Column<int>(type: "int", nullable: false),
                    Section = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteVersionTerms", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteVersionTerms_QuoteVersions_QuoteVersionId",
                        column: x => x.QuoteVersionId,
                        principalTable: "QuoteVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "QuoteVersionWarranties",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteVersionId = table.Column<int>(type: "int", nullable: false),
                    Brand = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MaterialWarranty = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false),
                    WorkmanshipWarranty = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteVersionWarranties", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteVersionWarranties_QuoteVersions_QuoteVersionId",
                        column: x => x.QuoteVersionId,
                        principalTable: "QuoteVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteVersionTerms_QuoteVersionId_Section_SortOrder",
                table: "QuoteVersionTerms",
                columns: new[] { "QuoteVersionId", "Section", "SortOrder" });

            migrationBuilder.CreateIndex(
                name: "IX_QuoteVersionWarranties_QuoteVersionId_Brand",
                table: "QuoteVersionWarranties",
                columns: new[] { "QuoteVersionId", "Brand" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "QuoteVersionTerms");

            migrationBuilder.DropTable(
                name: "QuoteVersionWarranties");
        }
    }
}
