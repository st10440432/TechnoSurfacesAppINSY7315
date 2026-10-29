using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfacesApp.Identity.Migrations
{
    /// <inheritdoc />
    public partial class AddLastSignInToUserAccount : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "LastSignInUtc",
                schema: "auth",
                table: "AspNetUsers",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastSignInUtc",
                schema: "auth",
                table: "AspNetUsers");
        }
    }
}
