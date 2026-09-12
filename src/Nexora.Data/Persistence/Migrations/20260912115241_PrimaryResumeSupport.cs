using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;
    /// <inheritdoc />
    public partial class PrimaryResumeSupport : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PrimaryResumeId",
                table: "user_profiles",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_profiles_PrimaryResumeId",
                table: "user_profiles",
                column: "PrimaryResumeId");

            migrationBuilder.AddForeignKey(
                name: "FK_user_profiles_resumes_PrimaryResumeId",
                table: "user_profiles",
                column: "PrimaryResumeId",
                principalTable: "resumes",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_user_profiles_resumes_PrimaryResumeId",
                table: "user_profiles");

            migrationBuilder.DropIndex(
                name: "IX_user_profiles_PrimaryResumeId",
                table: "user_profiles");

            migrationBuilder.DropColumn(
                name: "PrimaryResumeId",
                table: "user_profiles");
        }
}
