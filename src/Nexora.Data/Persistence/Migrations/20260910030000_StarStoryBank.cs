using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class StarStoryBank : Migration
    {
        private static readonly string[] StoryIndexColumns = ["UserId", "UpdatedAt"];

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "star_stories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    TagsJson = table.Column<string>(type: "jsonb", maxLength: 1000, nullable: false),
                    Situation = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Task = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Action = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    Result = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    LatestScore = table.Column<int>(type: "integer", nullable: true),
                    LatestEvaluationJson = table.Column<string>(type: "jsonb", nullable: true),
                    LatestModelVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LatestPromptVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LatestSchemaVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    LatestEvaluatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_star_stories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_star_stories_asp_net_users_UserId",
                        column: x => x.UserId,
                        principalTable: "asp_net_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_star_stories_UserId_UpdatedAt",
                table: "star_stories",
                columns: StoryIndexColumns);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "star_stories");
        }
    }
}
