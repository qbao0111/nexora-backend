using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration arrays are emitted once per migration invocation.

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class ExternalAccountDeletionVerification : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "external_deletion_verifications",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_external_deletion_verifications", x => x.Id);
                table.ForeignKey(
                    name: "FK_external_deletion_verifications_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_external_deletion_verifications_TokenHash",
            table: "external_deletion_verifications",
            column: "TokenHash",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_external_deletion_verifications_UserId_ExpiresAt",
            table: "external_deletion_verifications",
            columns: new[] { "UserId", "ExpiresAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "external_deletion_verifications");
    }
}
