using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Generated EF migration uses provider-required constant arrays.

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase2PlanCatalogue : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "SortOrder",
            table: "plans",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AlterColumn<int>(
            name: "InterviewQuota",
            table: "plan_prices",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<int>(
            name: "DurationDays",
            table: "plan_prices",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<int>(
            name: "InterviewQuota",
            table: "orders",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<int>(
            name: "DurationDays",
            table: "orders",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<int>(
            name: "InterviewLimit",
            table: "entitlements",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.InsertData(
            table: "plans",
            columns: new[] { "Id", "Code", "CreatedAt", "IsActive", "Name", "SortOrder" },
            values: new object[,]
            {
                { new Guid("00000000-0000-0000-0000-000000000001"), "free", new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Free", 0 },
                { new Guid("00000000-0000-0000-0000-000000000002"), "basic", new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Basic", 1 },
                { new Guid("00000000-0000-0000-0000-000000000003"), "weekly", new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Weekly", 2 },
                { new Guid("00000000-0000-0000-0000-000000000004"), "pro", new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), true, "Pro", 3 }
            });

        migrationBuilder.InsertData(
            table: "plan_prices",
            columns: new[] { "Id", "AmountMinor", "CreatedAt", "Currency", "DurationDays", "InterviewQuota", "IsActive", "PlanId" },
            values: new object[,]
            {
                { new Guid("10000000-0000-0000-0000-000000000001"), 0L, new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "VND", null, 1, true, new Guid("00000000-0000-0000-0000-000000000001") },
                { new Guid("10000000-0000-0000-0000-000000000002"), 49000L, new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "VND", 3, 3, true, new Guid("00000000-0000-0000-0000-000000000002") },
                { new Guid("10000000-0000-0000-0000-000000000003"), 189000L, new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "VND", 14, 20, true, new Guid("00000000-0000-0000-0000-000000000003") },
                { new Guid("10000000-0000-0000-0000-000000000004"), 599000L, new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "VND", 90, null, true, new Guid("00000000-0000-0000-0000-000000000004") }
            });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DeleteData(
            table: "plan_prices",
            keyColumn: "Id",
            keyValue: new Guid("10000000-0000-0000-0000-000000000001"));

        migrationBuilder.DeleteData(
            table: "plan_prices",
            keyColumn: "Id",
            keyValue: new Guid("10000000-0000-0000-0000-000000000002"));

        migrationBuilder.DeleteData(
            table: "plan_prices",
            keyColumn: "Id",
            keyValue: new Guid("10000000-0000-0000-0000-000000000003"));

        migrationBuilder.DeleteData(
            table: "plan_prices",
            keyColumn: "Id",
            keyValue: new Guid("10000000-0000-0000-0000-000000000004"));

        migrationBuilder.DeleteData(
            table: "plans",
            keyColumn: "Id",
            keyValue: new Guid("00000000-0000-0000-0000-000000000001"));

        migrationBuilder.DeleteData(
            table: "plans",
            keyColumn: "Id",
            keyValue: new Guid("00000000-0000-0000-0000-000000000002"));

        migrationBuilder.DeleteData(
            table: "plans",
            keyColumn: "Id",
            keyValue: new Guid("00000000-0000-0000-0000-000000000003"));

        migrationBuilder.DeleteData(
            table: "plans",
            keyColumn: "Id",
            keyValue: new Guid("00000000-0000-0000-0000-000000000004"));

        migrationBuilder.DropColumn(
            name: "SortOrder",
            table: "plans");

        migrationBuilder.AlterColumn<int>(
            name: "InterviewQuota",
            table: "plan_prices",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "DurationDays",
            table: "plan_prices",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "InterviewQuota",
            table: "orders",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "DurationDays",
            table: "orders",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<int>(
            name: "InterviewLimit",
            table: "entitlements",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);
    }
}
