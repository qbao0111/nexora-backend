using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProviderResultCheckpoints : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "FailureRetryHint",
            table: "provider_call_reservations",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ResultFingerprint",
            table: "provider_call_reservations",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ResultJson",
            table: "provider_call_reservations",
            type: "text",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "FailureRetryHint",
            table: "provider_call_reservations");

        migrationBuilder.DropColumn(
            name: "ResultFingerprint",
            table: "provider_call_reservations");

        migrationBuilder.DropColumn(
            name: "ResultJson",
            table: "provider_call_reservations");
    }
}
