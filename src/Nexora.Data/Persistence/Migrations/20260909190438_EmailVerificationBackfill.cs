using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class EmailVerificationBackfill : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            UPDATE "asp_net_users"
            SET "EmailConfirmed" = TRUE
            WHERE "EmailConfirmed" = FALSE
              AND "CreatedAt" < '2026-09-10T00:00:00+00:00';
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
