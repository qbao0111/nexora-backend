using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Prefer 'static readonly' fields over constant array arguments

namespace Nexora.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IdentityAdminFoundation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                table: "asp_net_users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.Sql(
                """
                DELETE FROM asp_net_user_roles WHERE "RoleId" NOT IN ('50000000-0000-0000-0000-000000000001', '50000000-0000-0000-0000-000000000002');
                DELETE FROM asp_net_roles WHERE "Id" NOT IN ('50000000-0000-0000-0000-000000000001', '50000000-0000-0000-0000-000000000002');
                """);

            migrationBuilder.InsertData(
                table: "asp_net_roles",
                columns: new[] { "Id", "ConcurrencyStamp", "Name", "NormalizedName" },
                values: new object[,]
                {
                    { new Guid("50000000-0000-0000-0000-000000000001"), "50000000-0000-0000-0000-000000000001", "User", "USER" },
                    { new Guid("50000000-0000-0000-0000-000000000002"), "50000000-0000-0000-0000-000000000002", "Admin", "ADMIN" }
                });

            migrationBuilder.Sql(
                """
                INSERT INTO asp_net_user_roles ("UserId", "RoleId")
                SELECT u."Id", '50000000-0000-0000-0000-000000000001'
                FROM asp_net_users u
                WHERE NOT EXISTS (
                    SELECT 1 FROM asp_net_user_roles ur
                    WHERE ur."UserId" = u."Id" AND ur."RoleId" = '50000000-0000-0000-0000-000000000001'
                );
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "asp_net_roles",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000001"));

            migrationBuilder.DeleteData(
                table: "asp_net_roles",
                keyColumn: "Id",
                keyValue: new Guid("50000000-0000-0000-0000-000000000002"));

            migrationBuilder.DropColumn(
                name: "IsActive",
                table: "asp_net_users");
        }
    }
}
