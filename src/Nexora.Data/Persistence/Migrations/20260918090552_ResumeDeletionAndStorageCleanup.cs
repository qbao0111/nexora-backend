using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class ResumeDeletionAndStorageCleanup : Migration
{
    private static readonly string[] ResumeStorageCleanupIndexColumns =
        ["DeletedAt", "StorageDeletedAt", "StorageDeleteNextAttemptAt"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "DeletedAt",
            table: "resumes",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "StorageDeleteAttempts",
            table: "resumes",
            type: "integer",
            nullable: false,
            defaultValue: 0);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "StorageDeleteNextAttemptAt",
            table: "resumes",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "StorageDeletedAt",
            table: "resumes",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_resumes_DeletedAt_StorageDeletedAt_StorageDeleteNextAttempt~",
            table: "resumes",
            columns: ResumeStorageCleanupIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_resumes_DeletedAt_StorageDeletedAt_StorageDeleteNextAttempt~",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "DeletedAt",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "StorageDeleteAttempts",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "StorageDeleteNextAttemptAt",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "StorageDeletedAt",
            table: "resumes");
    }
}
