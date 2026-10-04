using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace TechnoSurfaces.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class MakeAuditEntriesInsertOnly : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NFR-04: the audit trail is insert-only in the database itself, not just
            // by having no update path in the code. Any UPDATE or DELETE is rejected,
            // whoever issues it.
            // Created through EXEC because the idempotent migration script wraps each
            // migration in IF NOT EXISTS ... BEGIN ... END, and CREATE TRIGGER must be
            // the first statement in a batch. Single quotes inside are doubled.
            migrationBuilder.Sql(@"
                EXEC(N'CREATE TRIGGER dbo.TR_AuditEntries_InsertOnly
                ON dbo.AuditEntries
                INSTEAD OF UPDATE, DELETE
                AS
                BEGIN
                    SET NOCOUNT ON;
                    THROW 51000, ''AuditEntries is insert-only: audit records cannot be changed or deleted.'', 1;
                END')");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER IF EXISTS dbo.TR_AuditEntries_InsertOnly;");
        }
    }
}
