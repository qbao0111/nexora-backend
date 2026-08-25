using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Generated EF migration uses provider-required constant arrays.

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase3CoreAiPractice : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "job_descriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Content = table.Column<string>(type: "character varying(30000)", maxLength: 30000, nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_job_descriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_job_descriptions_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "stored_files",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                StorageKey = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                ContentType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Size = table.Column<long>(type: "bigint", nullable: false),
                Checksum = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_stored_files", x => x.Id);
                table.ForeignKey(
                    name: "FK_stored_files_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "resumes",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                StoredFileId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ExtractedText = table.Column<string>(type: "text", nullable: true),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resumes", x => x.Id);
                table.ForeignKey(
                    name: "FK_resumes_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_resumes_stored_files_StoredFileId",
                    column: x => x.StoredFileId,
                    principalTable: "stored_files",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "interview_sessions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ResumeId = table.Column<Guid>(type: "uuid", nullable: true),
                JobDescriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                ReservationEventId = table.Column<Guid>(type: "uuid", nullable: false),
                Role = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Seniority = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                InterviewType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Difficulty = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Version = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_interview_sessions", x => x.Id);
                table.ForeignKey(
                    name: "FK_interview_sessions_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_sessions_job_descriptions_JobDescriptionId",
                    column: x => x.JobDescriptionId,
                    principalTable: "job_descriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_sessions_resumes_ResumeId",
                    column: x => x.ResumeId,
                    principalTable: "resumes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_sessions_usage_events_ReservationEventId",
                    column: x => x.ReservationEventId,
                    principalTable: "usage_events",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "resume_analyses",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                ResumeId = table.Column<Guid>(type: "uuid", nullable: false),
                JobDescriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                ResumeVersion = table.Column<int>(type: "integer", nullable: false),
                JobDescriptionVersion = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                ModelVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                PromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Result = table.Column<string>(type: "jsonb", nullable: true),
                ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_resume_analyses", x => x.Id);
                table.ForeignKey(
                    name: "FK_resume_analyses_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_resume_analyses_job_descriptions_JobDescriptionId",
                    column: x => x.JobDescriptionId,
                    principalTable: "job_descriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_resume_analyses_resumes_ResumeId",
                    column: x => x.ResumeId,
                    principalTable: "resumes",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "interview_questions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                InterviewSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                Sequence = table.Column<int>(type: "integer", nullable: false),
                Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                PromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                ModelVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_interview_questions", x => x.Id);
                table.ForeignKey(
                    name: "FK_interview_questions_interview_sessions_InterviewSessionId",
                    column: x => x.InterviewSessionId,
                    principalTable: "interview_sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "interview_reports",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                InterviewSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                OverallScore = table.Column<int>(type: "integer", nullable: false),
                Rubric = table.Column<string>(type: "jsonb", nullable: false),
                Strengths = table.Column<string>(type: "jsonb", nullable: false),
                Gaps = table.Column<string>(type: "jsonb", nullable: false),
                ActionPlan = table.Column<string>(type: "jsonb", nullable: false),
                Disclaimer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                ModelVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                PromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                RubricVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                SchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_interview_reports", x => x.Id);
                table.ForeignKey(
                    name: "FK_interview_reports_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_reports_interview_sessions_InterviewSessionId",
                    column: x => x.InterviewSessionId,
                    principalTable: "interview_sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "interview_answers",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                InterviewSessionId = table.Column<Guid>(type: "uuid", nullable: false),
                QuestionId = table.Column<Guid>(type: "uuid", nullable: false),
                Content = table.Column<string>(type: "character varying(12000)", maxLength: 12000, nullable: false),
                DurationSeconds = table.Column<int>(type: "integer", nullable: true),
                Evaluation = table.Column<string>(type: "jsonb", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_interview_answers", x => x.Id);
                table.ForeignKey(
                    name: "FK_interview_answers_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_answers_interview_questions_QuestionId",
                    column: x => x.QuestionId,
                    principalTable: "interview_questions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_interview_answers_interview_sessions_InterviewSessionId",
                    column: x => x.InterviewSessionId,
                    principalTable: "interview_sessions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_interview_answers_InterviewSessionId_QuestionId",
            table: "interview_answers",
            columns: new[] { "InterviewSessionId", "QuestionId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_interview_answers_QuestionId",
            table: "interview_answers",
            column: "QuestionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_interview_answers_UserId_CreatedAt",
            table: "interview_answers",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_interview_questions_InterviewSessionId_Sequence",
            table: "interview_questions",
            columns: new[] { "InterviewSessionId", "Sequence" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_interview_reports_InterviewSessionId",
            table: "interview_reports",
            column: "InterviewSessionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_interview_reports_UserId_CreatedAt",
            table: "interview_reports",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_interview_sessions_JobDescriptionId",
            table: "interview_sessions",
            column: "JobDescriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_interview_sessions_ReservationEventId",
            table: "interview_sessions",
            column: "ReservationEventId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_interview_sessions_ResumeId",
            table: "interview_sessions",
            column: "ResumeId");

        migrationBuilder.CreateIndex(
            name: "IX_interview_sessions_UserId_CreatedAt",
            table: "interview_sessions",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_job_descriptions_UserId_CreatedAt",
            table: "job_descriptions",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_resume_analyses_JobDescriptionId",
            table: "resume_analyses",
            column: "JobDescriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_resume_analyses_ResumeId",
            table: "resume_analyses",
            column: "ResumeId");

        migrationBuilder.CreateIndex(
            name: "IX_resume_analyses_UserId_CreatedAt",
            table: "resume_analyses",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_resumes_StoredFileId",
            table: "resumes",
            column: "StoredFileId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_resumes_UserId_CreatedAt",
            table: "resumes",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_stored_files_StorageKey",
            table: "stored_files",
            column: "StorageKey",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_stored_files_UserId_CreatedAt",
            table: "stored_files",
            columns: new[] { "UserId", "CreatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "interview_answers");

        migrationBuilder.DropTable(
            name: "interview_reports");

        migrationBuilder.DropTable(
            name: "resume_analyses");

        migrationBuilder.DropTable(
            name: "interview_questions");

        migrationBuilder.DropTable(
            name: "interview_sessions");

        migrationBuilder.DropTable(
            name: "job_descriptions");

        migrationBuilder.DropTable(
            name: "resumes");

        migrationBuilder.DropTable(
            name: "stored_files");
    }
}
