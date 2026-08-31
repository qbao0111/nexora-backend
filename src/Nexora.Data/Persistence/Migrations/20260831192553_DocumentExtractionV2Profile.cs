using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class DocumentExtractionV2Profile : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ProfileModelVersion",
            table: "resumes",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfilePromptVersion",
            table: "resumes",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileSchemaVersion",
            table: "resumes",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "StructuredProfile",
            table: "resumes",
            type: "jsonb",
            nullable: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ProfileModelVersion",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "ProfilePromptVersion",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "ProfileSchemaVersion",
            table: "resumes");

        migrationBuilder.DropColumn(
            name: "StructuredProfile",
            table: "resumes");
    }
}
