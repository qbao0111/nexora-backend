using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class InterviewQuestionContractV1 : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "Kind",
            table: "interview_questions",
            type: "character varying(16)",
            maxLength: 16,
            nullable: false,
            defaultValue: "primary");

        migrationBuilder.AddColumn<Guid>(
            name: "ParentQuestionId",
            table: "interview_questions",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "Topic",
            table: "interview_questions",
            type: "character varying(80)",
            maxLength: 80,
            nullable: false,
            defaultValue: "self_introduction");

        migrationBuilder.Sql("""
            UPDATE "interview_questions" AS q
            SET "Kind" = CASE
                    WHEN q."Sequence" = 1 OR NOT EXISTS (
                        SELECT 1
                        FROM "interview_questions" AS root
                        WHERE root."InterviewSessionId" = q."InterviewSessionId" AND root."Sequence" = 1)
                    THEN 'primary'
                    ELSE 'followup'
                END,
                "Topic" = CASE lower(s."InterviewType")
                    WHEN 'behavioral' THEN 'behavioral_star'
                    WHEN 'technical' THEN 'technical'
                    WHEN 'cv_targeted' THEN 'cv_targeted'
                    WHEN 'jd_targeted' THEN 'jd_targeted'
                    WHEN 'scenario' THEN 'scenario'
                    WHEN 'motivation_role_fit' THEN 'motivation_role_fit'
                    WHEN 'self_introduction' THEN 'self_introduction'
                    ELSE 'self_introduction'
                END
            FROM "interview_sessions" AS s
            WHERE s."Id" = q."InterviewSessionId";

            UPDATE "interview_questions" AS child
            SET "ParentQuestionId" = root."Id"
            FROM "interview_questions" AS root
            WHERE child."Kind" = 'followup'
              AND root."InterviewSessionId" = child."InterviewSessionId"
              AND root."Sequence" = 1;
            """);

        migrationBuilder.CreateIndex(
            name: "IX_interview_questions_ParentQuestionId",
            table: "interview_questions",
            column: "ParentQuestionId");

        migrationBuilder.AddCheckConstraint(
            name: "CK_interview_questions_kind",
            table: "interview_questions",
            sql: "\"Kind\" IN ('primary', 'followup')");

        migrationBuilder.AddCheckConstraint(
            name: "CK_interview_questions_parent",
            table: "interview_questions",
            sql: "((\"Kind\" = 'primary' AND \"ParentQuestionId\" IS NULL) OR (\"Kind\" = 'followup' AND \"ParentQuestionId\" IS NOT NULL))");

        migrationBuilder.AddForeignKey(
            name: "FK_interview_questions_interview_questions_ParentQuestionId",
            table: "interview_questions",
            column: "ParentQuestionId",
            principalTable: "interview_questions",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_interview_questions_interview_questions_ParentQuestionId",
            table: "interview_questions");

        migrationBuilder.DropIndex(
            name: "IX_interview_questions_ParentQuestionId",
            table: "interview_questions");

        migrationBuilder.DropCheckConstraint(
            name: "CK_interview_questions_kind",
            table: "interview_questions");

        migrationBuilder.DropCheckConstraint(
            name: "CK_interview_questions_parent",
            table: "interview_questions");

        migrationBuilder.DropColumn(
            name: "Kind",
            table: "interview_questions");

        migrationBuilder.DropColumn(
            name: "ParentQuestionId",
            table: "interview_questions");

        migrationBuilder.DropColumn(
            name: "Topic",
            table: "interview_questions");
    }
}
