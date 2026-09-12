using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class InterviewLanguage : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "InterviewLanguage",
            table: "interview_sessions",
            type: "character varying(10)",
            maxLength: 10,
            nullable: false,
            defaultValue: "vi-VN");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "InterviewLanguage",
            table: "interview_sessions");
    }
}
