using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // EF-generated migration arrays are executed once per migration.

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class PrivacyRetentionLifecycle : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "retention_checkpoints",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false),
                NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                DryRun = table.Column<bool>(type: "boolean", nullable: false),
                Examined = table.Column<int>(type: "integer", nullable: false),
                Eligible = table.Column<int>(type: "integer", nullable: false),
                Removed = table.Column<int>(type: "integer", nullable: false),
                Skipped = table.Column<int>(type: "integer", nullable: false),
                Failed = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_retention_checkpoints", x => x.Id);
                table.CheckConstraint("CK_retention_checkpoint_id", "\"Id\" = 1");
            });

        migrationBuilder.CreateTable(
            name: "retention_holds",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: true),
                ReasonCode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_retention_holds", x => x.Id);
                table.CheckConstraint("CK_retention_hold_reason", "\"ReasonCode\" IN ('legal', 'dispute', 'fraud', 'accounting', 'security')");
                table.ForeignKey(
                    name: "FK_retention_holds_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.InsertData(
            table: "retention_checkpoints",
            columns: new[] { "Id", "ConsecutiveFailures", "DryRun", "Eligible", "Examined", "Failed", "LastRunAt", "NextRunAt", "Removed", "Skipped", "Status" },
            values: new object[] { 1, 0, true, 0, 0, 0, null, null, 0, 0, "not_run" });

        migrationBuilder.CreateIndex(
            name: "IX_external_deletion_verifications_ExpiresAt_Id",
            table: "external_deletion_verifications",
            columns: new[] { "ExpiresAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_data_privacy_requests_Status_CompletedAt_Id",
            table: "data_privacy_requests",
            columns: new[] { "Status", "CompletedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_retention_holds_ReleasedAt_UserId",
            table: "retention_holds",
            columns: new[] { "ReleasedAt", "UserId" });

        migrationBuilder.CreateIndex(
            name: "IX_retention_holds_UserId",
            table: "retention_holds",
            column: "UserId");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "retention_checkpoints");

        migrationBuilder.DropTable(
            name: "retention_holds");

        migrationBuilder.DropIndex(
            name: "IX_external_deletion_verifications_ExpiresAt_Id",
            table: "external_deletion_verifications");

        migrationBuilder.DropIndex(
            name: "IX_data_privacy_requests_Status_CompletedAt_Id",
            table: "data_privacy_requests");
    }
}
