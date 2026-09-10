using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class B9CareerGoal : Migration
{
    private static readonly string[] UserIdCreatedAtIndexColumns = ["UserId", "CreatedAt"];

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "career_goals",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                TargetRole = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                Seniority = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Industry = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                TargetCompany = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: true),
                TargetJobDescriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                TargetDate = table.Column<DateOnly>(type: "date", nullable: true),
                Active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_career_goals", x => x.Id);
                table.ForeignKey(
                    name: "FK_career_goals_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_career_goals_job_descriptions_TargetJobDescriptionId",
                    column: x => x.TargetJobDescriptionId,
                    principalTable: "job_descriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_career_goals_one_active_per_user",
            table: "career_goals",
            column: "UserId",
            unique: true,
            filter: "\"Active\" = TRUE");

        migrationBuilder.CreateIndex(
            name: "IX_career_goals_TargetJobDescriptionId",
            table: "career_goals",
            column: "TargetJobDescriptionId");

        migrationBuilder.CreateIndex(
            name: "IX_career_goals_UserId_CreatedAt",
            table: "career_goals",
            columns: UserIdCreatedAtIndexColumns);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "career_goals");
    }
}
