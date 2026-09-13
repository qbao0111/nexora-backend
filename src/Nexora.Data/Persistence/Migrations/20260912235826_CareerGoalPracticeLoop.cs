using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class CareerGoalPracticeLoop : Migration
{
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "CareerGoalId",
                table: "interview_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "FocusTopic",
                table: "interview_sessions",
                type: "character varying(80)",
                maxLength: 80,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PracticeReason",
                table: "interview_sessions",
                type: "character varying(40)",
                maxLength: 40,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceInterviewId",
                table: "interview_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceQuestionId",
                table: "interview_sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_interview_sessions_CareerGoalId",
                table: "interview_sessions",
                column: "CareerGoalId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_sessions_SourceInterviewId",
                table: "interview_sessions",
                column: "SourceInterviewId");

            migrationBuilder.CreateIndex(
                name: "IX_interview_sessions_SourceQuestionId",
                table: "interview_sessions",
                column: "SourceQuestionId");

            migrationBuilder.AddForeignKey(
                name: "FK_interview_sessions_career_goals_CareerGoalId",
                table: "interview_sessions",
                column: "CareerGoalId",
                principalTable: "career_goals",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_interview_sessions_interview_questions_SourceQuestionId",
                table: "interview_sessions",
                column: "SourceQuestionId",
                principalTable: "interview_questions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_interview_sessions_interview_sessions_SourceInterviewId",
                table: "interview_sessions",
                column: "SourceInterviewId",
                principalTable: "interview_sessions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_interview_sessions_career_goals_CareerGoalId",
                table: "interview_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_interview_sessions_interview_questions_SourceQuestionId",
                table: "interview_sessions");

            migrationBuilder.DropForeignKey(
                name: "FK_interview_sessions_interview_sessions_SourceInterviewId",
                table: "interview_sessions");

            migrationBuilder.DropIndex(
                name: "IX_interview_sessions_CareerGoalId",
                table: "interview_sessions");

            migrationBuilder.DropIndex(
                name: "IX_interview_sessions_SourceInterviewId",
                table: "interview_sessions");

            migrationBuilder.DropIndex(
                name: "IX_interview_sessions_SourceQuestionId",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "CareerGoalId",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "FocusTopic",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "PracticeReason",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "SourceInterviewId",
                table: "interview_sessions");

            migrationBuilder.DropColumn(
                name: "SourceQuestionId",
                table: "interview_sessions");
        }
}
