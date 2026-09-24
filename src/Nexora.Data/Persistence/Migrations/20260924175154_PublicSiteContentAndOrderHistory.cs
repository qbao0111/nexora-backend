using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration arrays are emitted once per migration invocation.

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class PublicSiteContentAndOrderHistory : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_orders_UserId_CreatedAt",
            table: "orders");

        migrationBuilder.CreateTable(
            name: "site_assets",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UploadedBy = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_site_assets", x => x.Id);
                table.ForeignKey(
                    name: "FK_site_assets_asp_net_users_UploadedBy",
                    column: x => x.UploadedBy,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "site_pages",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Key = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                DraftTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                DraftBodyMarkdown = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: true),
                DraftAboutJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: true),
                DraftEffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                PublishedTitle = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                PublishedBodyMarkdown = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: true),
                PublishedAboutJson = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: true),
                PublishedEffectiveAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_site_pages", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "site_settings",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ContactEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                BrandDescription = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                FacebookUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                TiktokUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                SupportAvailabilityEnabled = table.Column<bool>(type: "boolean", nullable: false),
                SupportLabel = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                MadeInVietnamEnabled = table.Column<bool>(type: "boolean", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_site_settings", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_orders_UserId_CreatedAt_Id",
            table: "orders",
            columns: new[] { "UserId", "CreatedAt", "Id" });

        migrationBuilder.CreateIndex(
            name: "IX_site_assets_StorageKey",
            table: "site_assets",
            column: "StorageKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_site_assets_UploadedBy",
            table: "site_assets",
            column: "UploadedBy");

        migrationBuilder.CreateIndex(
            name: "IX_site_pages_Key",
            table: "site_pages",
            column: "Key",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "site_assets");

        migrationBuilder.DropTable(
            name: "site_pages");

        migrationBuilder.DropTable(
            name: "site_settings");

        migrationBuilder.DropIndex(
            name: "IX_orders_UserId_CreatedAt_Id",
            table: "orders");

        migrationBuilder.CreateIndex(
            name: "IX_orders_UserId_CreatedAt",
            table: "orders",
            columns: new[] { "UserId", "CreatedAt" });
    }
}
