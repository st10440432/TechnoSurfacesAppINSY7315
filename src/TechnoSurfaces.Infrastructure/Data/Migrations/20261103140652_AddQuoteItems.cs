using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQuoteItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuoteItemId",
                table: "CostingLines",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "QuoteItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    QuoteVersionId = table.Column<int>(type: "int", nullable: false),
                    Number = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    Name = table.Column<string>(type: "nvarchar(400)", maxLength: 400, nullable: false),
                    Quantity = table.Column<decimal>(type: "decimal(18,4)", precision: 18, scale: 4, nullable: false),
                    MarkupPercent = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    SortOrder = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_QuoteItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_QuoteItems_QuoteVersions_QuoteVersionId",
                        column: x => x.QuoteVersionId,
                        principalTable: "QuoteVersions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CostingLines_QuoteItemId",
                table: "CostingLines",
                column: "QuoteItemId");

            migrationBuilder.CreateIndex(
                name: "IX_QuoteItems_QuoteVersionId",
                table: "QuoteItems",
                column: "QuoteVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_CostingLines_QuoteItems_QuoteItemId",
                table: "CostingLines",
                column: "QuoteItemId",
                principalTable: "QuoteItems",
                principalColumn: "Id");

            // Every version that already exists becomes one item at its own markup,
            // holding all its costing lines, so each quote totals exactly as before.
            // Run through EXEC because the idempotent script wraps each migration in an
            // IF block, which SQL Server compiles before the new table exists.
            migrationBuilder.Sql(@"
EXEC(N'INSERT INTO dbo.QuoteItems (QuoteVersionId, Number, Name, Quantity, MarkupPercent, SortOrder)
SELECT v.Id, NULL, N''Item 1'', 1, v.MarkupPercent, 1
FROM dbo.QuoteVersions v
WHERE NOT EXISTS (SELECT 1 FROM dbo.QuoteItems i WHERE i.QuoteVersionId = v.Id)');");

            migrationBuilder.Sql(@"
EXEC(N'UPDATE l SET QuoteItemId = i.Id
FROM dbo.CostingLines l
JOIN dbo.QuoteItems i ON i.QuoteVersionId = l.QuoteVersionId AND i.SortOrder = 1
WHERE l.QuoteItemId IS NULL');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CostingLines_QuoteItems_QuoteItemId",
                table: "CostingLines");

            migrationBuilder.DropTable(
                name: "QuoteItems");

            migrationBuilder.DropIndex(
                name: "IX_CostingLines_QuoteItemId",
                table: "CostingLines");

            migrationBuilder.DropColumn(
                name: "QuoteItemId",
                table: "CostingLines");
        }
    }
}
