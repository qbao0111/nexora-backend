using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class CvAnalysisV2 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AlterColumn<int>(
            name: "JobDescriptionVersion",
            table: "resume_analyses",
            type: "integer",
            nullable: true,
            oldClrType: typeof(int),
            oldType: "integer");

        migrationBuilder.AlterColumn<Guid>(
            name: "JobDescriptionId",
            table: "resume_analyses",
            type: "uuid",
            nullable: true,
            oldClrType: typeof(Guid),
            oldType: "uuid");

        migrationBuilder.AddColumn<string>(
            name: "ContextJson",
            table: "resume_analyses",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Mode",
            table: "resume_analyses",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.Sql("UPDATE resume_analyses SET \"Mode\" = 'job_targeted' WHERE \"Mode\" IS NULL;");

        migrationBuilder.AlterColumn<string>(
            name: "Mode",
            table: "resume_analyses",
            type: "character varying(32)",
            maxLength: 32,
            nullable: false,
            oldClrType: typeof(string),
            oldType: "character varying(32)",
            oldMaxLength: 32,
            oldNullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileModelVersion",
            table: "resume_analyses",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfilePromptVersion",
            table: "resume_analyses",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileSchemaVersion",
            table: "resume_analyses",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "ProfileSnapshot",
            table: "resume_analyses",
            type: "jsonb",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "RubricVersion",
            table: "resume_analyses",
            type: "character varying(80)",
            maxLength: 80,
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "CK_resume_analyses_mode",
            table: "resume_analyses",
            sql: "\"Mode\" IN ('job_targeted', 'field_benchmark')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_resume_analyses_mode_job_description",
            table: "resume_analyses",
            sql: "((\"Mode\" = 'job_targeted' AND \"JobDescriptionId\" IS NOT NULL AND \"JobDescriptionVersion\" IS NOT NULL) OR (\"Mode\" = 'field_benchmark' AND \"JobDescriptionId\" IS NULL AND \"JobDescriptionVersion\" IS NULL))");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM resume_analyses WHERE "Mode" = 'field_benchmark') THEN
                    RAISE EXCEPTION 'Cannot downgrade CvAnalysisV2 while field-benchmark analyses exist.';
                END IF;
            END $$;
            """);

        migrationBuilder.DropCheckConstraint(
            name: "CK_resume_analyses_mode_job_description",
            table: "resume_analyses");

        migrationBuilder.DropCheckConstraint(
            name: "CK_resume_analyses_mode",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "ContextJson",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "Mode",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "ProfileModelVersion",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "ProfilePromptVersion",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "ProfileSchemaVersion",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "ProfileSnapshot",
            table: "resume_analyses");

        migrationBuilder.DropColumn(
            name: "RubricVersion",
            table: "resume_analyses");

        migrationBuilder.AlterColumn<int>(
            name: "JobDescriptionVersion",
            table: "resume_analyses",
            type: "integer",
            nullable: false,
            defaultValue: 0,
            oldClrType: typeof(int),
            oldType: "integer",
            oldNullable: true);

        migrationBuilder.AlterColumn<Guid>(
            name: "JobDescriptionId",
            table: "resume_analyses",
            type: "uuid",
            nullable: false,
            defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
            oldClrType: typeof(Guid),
            oldType: "uuid",
            oldNullable: true);
    }
}
