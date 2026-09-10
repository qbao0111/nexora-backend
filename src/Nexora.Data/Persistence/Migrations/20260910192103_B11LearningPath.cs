using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1861 // Prefer static readonly fields for constant arrays

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class B11LearningPath : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "learning_paths",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                CareerGoalId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_learning_paths", x => x.Id);
                table.ForeignKey(
                    name: "FK_learning_paths_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_learning_paths_career_goals_CareerGoalId",
                    column: x => x.CareerGoalId,
                    principalTable: "career_goals",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "learning_path_milestones",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                LearningPathId = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_learning_path_milestones", x => x.Id);
                table.ForeignKey(
                    name: "FK_learning_path_milestones_learning_paths_LearningPathId",
                    column: x => x.LearningPathId,
                    principalTable: "learning_paths",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateTable(
            name: "learning_path_activities",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                LearningPathId = table.Column<Guid>(type: "uuid", nullable: false),
                LearningPathMilestoneId = table.Column<Guid>(type: "uuid", nullable: false),
                ActivityKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Type = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Description = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                CompetencyCode = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: true),
                ExternalUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                Priority = table.Column<int>(type: "integer", nullable: false),
                SortOrder = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_learning_path_activities", x => x.Id);
                table.ForeignKey(
                    name: "FK_learning_path_activities_learning_path_milestones_LearningP~",
                    column: x => x.LearningPathMilestoneId,
                    principalTable: "learning_path_milestones",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_learning_path_activities_learning_paths_LearningPathId",
                    column: x => x.LearningPathId,
                    principalTable: "learning_paths",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_activities_LearningPathId_ActivityKey",
            table: "learning_path_activities",
            columns: new[] { "LearningPathId", "ActivityKey" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_activities_LearningPathId_Status_SortOrder",
            table: "learning_path_activities",
            columns: new[] { "LearningPathId", "Status", "SortOrder" });

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_activities_LearningPathMilestoneId",
            table: "learning_path_activities",
            column: "LearningPathMilestoneId");

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_activities_ResourceId",
            table: "learning_path_activities",
            column: "ResourceId");

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_milestones_LearningPathId_Code",
            table: "learning_path_milestones",
            columns: new[] { "LearningPathId", "Code" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_learning_path_milestones_LearningPathId_SortOrder",
            table: "learning_path_milestones",
            columns: new[] { "LearningPathId", "SortOrder" });

        migrationBuilder.CreateIndex(
            name: "IX_learning_paths_CareerGoalId",
            table: "learning_paths",
            column: "CareerGoalId");

        migrationBuilder.CreateIndex(
            name: "IX_learning_paths_one_per_user_career_goal",
            table: "learning_paths",
            columns: new[] { "UserId", "CareerGoalId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_learning_paths_UserId_UpdatedAt",
            table: "learning_paths",
            columns: new[] { "UserId", "UpdatedAt" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "learning_path_activities");

        migrationBuilder.DropTable(
            name: "learning_path_milestones");

        migrationBuilder.DropTable(
            name: "learning_paths");
    }
}
