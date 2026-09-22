using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class AsyncInterviewEvaluationAndProductFeedback : Migration
{
        private static readonly string[] InterviewQuestionIndexColumns = ["InterviewSessionId", "ReleasedAt"];
        private static readonly string[] FeedbackStatusIndexColumns = ["Status", "Consent", "Featured", "PublishedAt", "CreatedAt"];
        private static readonly string[] FeedbackUserIndexColumns = ["UserId", "CreatedAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReleasedAt",
                table: "interview_questions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "Evaluation",
                table: "interview_answers",
                type: "jsonb",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "jsonb");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EvaluationCompletedAt",
                table: "interview_answers",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationErrorCode",
                table: "interview_answers",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "EvaluationStatus",
                table: "interview_answers",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "queued");

            if (migrationBuilder.ActiveProvider.Contains("Npgsql", StringComparison.Ordinal))
            {
                migrationBuilder.Sql("UPDATE \"interview_questions\" SET \"ReleasedAt\" = \"CreatedAt\" WHERE \"ReleasedAt\" IS NULL;");
                migrationBuilder.Sql("UPDATE \"interview_answers\" SET \"EvaluationStatus\" = CASE WHEN \"Evaluation\" IS NOT NULL AND jsonb_typeof(\"Evaluation\") = 'object' AND \"Evaluation\"::text <> 'null' THEN 'ready' ELSE 'failed' END WHERE \"EvaluationStatus\" = 'queued';");
            }
            else
            {
                migrationBuilder.Sql("UPDATE \"interview_questions\" SET \"ReleasedAt\" = \"CreatedAt\" WHERE \"ReleasedAt\" IS NULL;");
                migrationBuilder.Sql("UPDATE \"interview_answers\" SET \"EvaluationStatus\" = CASE WHEN \"Evaluation\" IS NOT NULL AND length(trim(\"Evaluation\")) > 2 THEN 'ready' ELSE 'failed' END WHERE \"EvaluationStatus\" = 'queued';");
            }

            migrationBuilder.CreateTable(
                name: "product_feedback",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Consent = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Featured = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ModeratedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ModeratedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_feedback", x => x.Id);
                    table.CheckConstraint("CK_product_feedback_rating", "\"Rating\" BETWEEN 1 AND 5");
                    table.CheckConstraint("CK_product_feedback_status", "\"Status\" IN ('pending', 'approved', 'rejected')");
                    table.ForeignKey(
                        name: "FK_product_feedback_asp_net_users_ModeratedByUserId",
                        column: x => x.ModeratedByUserId,
                        principalTable: "asp_net_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_product_feedback_asp_net_users_UserId",
                        column: x => x.UserId,
                        principalTable: "asp_net_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_interview_questions_InterviewSessionId_ReleasedAt",
                table: "interview_questions",
                columns: InterviewQuestionIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_product_feedback_ModeratedByUserId",
                table: "product_feedback",
                column: "ModeratedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_product_feedback_one_current_per_user",
                table: "product_feedback",
                column: "UserId",
                unique: true,
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_product_feedback_Status_Consent_Featured_PublishedAt_CreatedAt",
                table: "product_feedback",
                columns: FeedbackStatusIndexColumns);

            migrationBuilder.CreateIndex(
                name: "IX_product_feedback_UserId_CreatedAt",
                table: "product_feedback",
                columns: FeedbackUserIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "product_feedback");

            migrationBuilder.DropIndex(
                name: "IX_interview_questions_InterviewSessionId_ReleasedAt",
                table: "interview_questions");

            migrationBuilder.DropColumn(
                name: "ReleasedAt",
                table: "interview_questions");

            migrationBuilder.DropColumn(
                name: "EvaluationCompletedAt",
                table: "interview_answers");

            migrationBuilder.DropColumn(
                name: "EvaluationErrorCode",
                table: "interview_answers");

            migrationBuilder.DropColumn(
                name: "EvaluationStatus",
                table: "interview_answers");

            migrationBuilder.AlterColumn<string>(
                name: "Evaluation",
                table: "interview_answers",
                type: "jsonb",
                nullable: false,
                defaultValue: "",
                oldClrType: typeof(string),
                oldType: "jsonb",
                oldNullable: true);
        }
}
