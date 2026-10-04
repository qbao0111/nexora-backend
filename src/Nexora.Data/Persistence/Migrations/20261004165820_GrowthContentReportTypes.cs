using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class GrowthContentReportTypes : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_content_reports_content_type",
            table: "content_reports");

        migrationBuilder.AddCheckConstraint(
            name: "CK_content_reports_content_type",
            table: "content_reports",
            sql: "\"ContentType\" IN ('interview_question', 'interview_answer_evaluation', 'interview_report', 'resume_analysis', 'scenario_evaluation', 'star_evaluation', 'learning_path', 'skill_profile')");
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "CK_content_reports_content_type",
            table: "content_reports");

        migrationBuilder.AddCheckConstraint(
            name: "CK_content_reports_content_type",
            table: "content_reports",
            sql: "\"ContentType\" IN ('interview_question', 'interview_answer_evaluation', 'interview_report', 'resume_analysis', 'scenario_evaluation', 'star_evaluation')");
    }
}
