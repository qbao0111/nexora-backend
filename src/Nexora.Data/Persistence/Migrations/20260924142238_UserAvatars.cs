using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class UserAvatars : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "AvatarContentType",
            table: "user_profiles",
            type: "character varying(32)",
            maxLength: 32,
            nullable: true);

        migrationBuilder.AddColumn<Guid>(
            name: "AvatarId",
            table: "user_profiles",
            type: "uuid",
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "AvatarStorageKey",
            table: "user_profiles",
            type: "character varying(512)",
            maxLength: 512,
            nullable: true);

        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "AvatarUpdatedAt",
            table: "user_profiles",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_user_profiles_AvatarId",
            table: "user_profiles",
            column: "AvatarId",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_user_profiles_AvatarId",
            table: "user_profiles");

        migrationBuilder.DropColumn(
            name: "AvatarContentType",
            table: "user_profiles");

        migrationBuilder.DropColumn(
            name: "AvatarId",
            table: "user_profiles");

        migrationBuilder.DropColumn(
            name: "AvatarStorageKey",
            table: "user_profiles");

        migrationBuilder.DropColumn(
            name: "AvatarUpdatedAt",
            table: "user_profiles");
    }
}
