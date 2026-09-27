using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class AiContentReports : Migration
{
    private static readonly string[] ContentTypeContentIdIndexColumns = ["ContentType", "ContentId"];
    private static readonly string[] ReporterCreatedAtIndexColumns = ["ReporterUserId", "CreatedAt"];
    private static readonly string[] StatusCreatedAtIdIndexColumns = ["Status", "CreatedAt", "Id"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "content_reports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ReporterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ContentId = table.Column<Guid>(type: "uuid", nullable: false),
                ReasonCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ContentSnapshot = table.Column<string>(type: "jsonb", maxLength: 40000, nullable: true),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                ResolutionCode = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                ResolutionNote = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_content_reports", x => x.Id);
                table.CheckConstraint("CK_content_reports_content_type", "\"ContentType\" IN ('interview_question', 'interview_answer_evaluation', 'interview_report', 'resume_analysis', 'scenario_evaluation', 'star_evaluation')");
                table.CheckConstraint("CK_content_reports_reason_code", "\"ReasonCode\" IN ('offensive', 'inaccurate', 'irrelevant', 'privacy_violation', 'discriminatory', 'other')");
                table.CheckConstraint("CK_content_reports_status", "\"Status\" IN ('pending', 'reviewing', 'resolved', 'dismissed')");
                table.ForeignKey(
                    name: "FK_content_reports_asp_net_users_ModeratorUserId",
                    column: x => x.ModeratorUserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_content_reports_asp_net_users_ReporterUserId",
                    column: x => x.ReporterUserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_content_reports_ContentType_ContentId",
            table: "content_reports",
            columns: ContentTypeContentIdIndexColumns);

        migrationBuilder.CreateIndex(
            name: "IX_content_reports_ModeratorUserId",
            table: "content_reports",
            column: "ModeratorUserId");

        migrationBuilder.CreateIndex(
            name: "IX_content_reports_ReporterUserId_CreatedAt",
            table: "content_reports",
            columns: ReporterCreatedAtIndexColumns);

        migrationBuilder.CreateIndex(
            name: "IX_content_reports_Status_CreatedAt_Id",
            table: "content_reports",
            columns: StatusCreatedAtIdIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "content_reports");
    }
}
