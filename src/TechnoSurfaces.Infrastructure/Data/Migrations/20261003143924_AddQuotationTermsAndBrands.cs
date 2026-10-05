using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuotationTermsAndBrands : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BrandId",
                table: "ProductLines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Brands",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Name = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    MaterialWarranty = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true),
                    WorkmanshipWarranty = table.Column<string>(type: "nvarchar(60)", maxLength: 60, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Brands", x => x.Id);
                    table.CheckConstraint("CK_Brand_WarrantyComplete", "([MaterialWarranty] IS NULL AND [WorkmanshipWarranty] IS NULL) OR ([MaterialWarranty] IS NOT NULL AND [WorkmanshipWarranty] IS NOT NULL)");
                });

            migrationBuilder.CreateTable(
                name: "QuotationTerms",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    Section = table.Column<int>(type: "int", nullable: false),
                    Text = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false),
                    IsActive = table.Column<bool>(type: "bit", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuotationTerms", x => x.Id);
                    table.CheckConstraint("CK_QuotationTerm_TextNotBlank", "[Text] <> ''");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProductLines_BrandId",
                table: "ProductLines",
                column: "BrandId");

            migrationBuilder.CreateIndex(
                name: "IX_Brands_Name",
                table: "Brands",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_QuotationTerms_IsActive_Section_SortOrder",
                table: "QuotationTerms",
                columns: new[] { "IsActive", "Section", "SortOrder" });

            migrationBuilder.AddForeignKey(
                name: "FK_ProductLines_Brands_BrandId",
                table: "ProductLines",
                column: "BrandId",
                principalTable: "Brands",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_ProductLines_Brands_BrandId",
                table: "ProductLines");

            migrationBuilder.DropTable(
                name: "Brands");

            migrationBuilder.DropTable(
                name: "QuotationTerms");

            migrationBuilder.DropIndex(
                name: "IX_ProductLines_BrandId",
                table: "ProductLines");

            migrationBuilder.DropColumn(
                name: "BrandId",
                table: "ProductLines");
        }
    }
}
