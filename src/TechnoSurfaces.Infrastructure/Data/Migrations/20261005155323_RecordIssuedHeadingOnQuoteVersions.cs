using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecordIssuedHeadingOnQuoteVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "IssuedAtUtc",
                table: "QuoteVersions",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedAttention",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedByUserId",
                table: "QuoteVersions",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedCompany",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedCustomerReference",
                table: "QuoteVersions",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedEmail",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedProject",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedSite",
                table: "QuoteVersions",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IssuedTel",
                table: "QuoteVersions",
                type: "nvarchar(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<DateOnly>(
                name: "IssuedValidUntil",
                table: "QuoteVersions",
                type: "date",
                nullable: true);

            // A quote that is approved now was issued with the details it still holds,
            // because a quote's details cannot change after approval until it is
            // reopened, and reopening clears the approval. Those versions are filled
            // in. A version issued before an earlier reopen cannot be known exactly
            // and is left empty; the screens say so rather than guess.
            //
            // Run through EXEC because the idempotent script wraps each migration in an
            // IF block, which SQL Server compiles before the new columns exist.
            migrationBuilder.Sql(@"
EXEC(N'UPDATE v SET
    IssuedAtUtc = q.ApprovedAtUtc,
    IssuedByUserId = q.ApprovedByUserId,
    IssuedAttention = c.FullName,
    IssuedCompany = cu.Name,
    IssuedTel = c.Phone,
    IssuedEmail = c.Email,
    IssuedSite = q.Site,
    IssuedProject = q.Project,
    IssuedCustomerReference = q.CustomerReference,
    IssuedValidUntil = q.ValidUntil
FROM dbo.QuoteVersions v
JOIN dbo.Quotes q ON q.Id = v.QuoteId
JOIN dbo.Contacts c ON c.Id = q.ContactId
JOIN dbo.Customers cu ON cu.Id = q.CustomerId
WHERE q.ApprovedAtUtc IS NOT NULL
  AND v.IsSealed = 1
  AND v.IssuedAtUtc IS NULL
  AND v.VersionNo = (SELECT MAX(x.VersionNo) FROM dbo.QuoteVersions x WHERE x.QuoteId = q.Id)');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IssuedAtUtc",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedAttention",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedByUserId",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedCompany",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedCustomerReference",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedEmail",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedProject",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedSite",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedTel",
                table: "QuoteVersions");

            migrationBuilder.DropColumn(
                name: "IssuedValidUntil",
                table: "QuoteVersions");
        }
    }
}
