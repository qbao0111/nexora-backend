using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase4PrivacyHardening : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "asp_net_users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletionRequestedAt",
            table: "asp_net_users",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateTable(
            name: "data_privacy_requests",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Attempts = table.Column<int>(type: "integer", nullable: false),
                ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_data_privacy_requests", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_data_privacy_requests_Status_NextAttemptAt",
            table: "data_privacy_requests",
            columns: ["Status", "NextAttemptAt"]);

        migrationBuilder.CreateIndex(
            name: "IX_data_privacy_requests_UserId_IdempotencyKey",
            table: "data_privacy_requests",
            columns: ["UserId", "IdempotencyKey"],
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "data_privacy_requests");

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "asp_net_users");

        migrationBuilder.DropColumn(
            name: "DeletionRequestedAt",
            table: "asp_net_users");
    }
}
