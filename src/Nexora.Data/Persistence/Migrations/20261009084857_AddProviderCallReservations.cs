using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // EF-generated immutable migration arrays.

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class AddProviderCallReservations : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "provider_call_reservations",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                JobId = table.Column<Guid>(type: "uuid", nullable: false),
                Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                OperationKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                Attempt = table.Column<int>(type: "integer", nullable: false),
                ReservedTokens = table.Column<long>(type: "bigint", nullable: false),
                ActualPromptTokens = table.Column<long>(type: "bigint", nullable: true),
                ActualCompletionTokens = table.Column<long>(type: "bigint", nullable: true),
                StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                FailureKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                CooldownUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_provider_call_reservations", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_provider_call_reservations_JobId_Purpose_OperationKey_Attem~",
            table: "provider_call_reservations",
            columns: new[] { "JobId", "Purpose", "OperationKey", "Attempt" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_provider_call_reservations_StartedAt",
            table: "provider_call_reservations",
            column: "StartedAt");

        migrationBuilder.CreateIndex(
            name: "IX_provider_call_reservations_UserId_StartedAt",
            table: "provider_call_reservations",
            columns: new[] { "UserId", "StartedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "provider_call_reservations");
    }
}
