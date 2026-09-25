using System;
using Microsoft.EntityFrameworkCore.Migrations;

#pragma warning disable CA1861 // EF-generated migration arrays are emitted once per migration invocation.

#nullable disable

namespace Nexora.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PaymentExpiration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ExpiresAt",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_Status_ExpiresAt",
                table: "orders",
                columns: new[] { "Status", "ExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_orders_Status_ExpiresAt",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ExpiresAt",
                table: "orders");
        }
    }
}
