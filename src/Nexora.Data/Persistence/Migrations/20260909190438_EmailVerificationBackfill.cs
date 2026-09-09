using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class EmailVerificationBackfill : Migration
{
    public const string BackfillSql =
        """
        UPDATE "asp_net_users"
        SET "EmailConfirmed" = TRUE
        WHERE "EmailConfirmed" = FALSE
          AND (
            EXISTS (
              SELECT 1
              FROM "refresh_tokens" r
              WHERE r."UserId" = "asp_net_users"."Id"
            )
            OR EXISTS (
              SELECT 1
              FROM "entitlements" e
              WHERE e."UserId" = "asp_net_users"."Id"
            )
            OR EXISTS (
              SELECT 1
              FROM "subscriptions" s
              WHERE s."UserId" = "asp_net_users"."Id"
            )
          );
        """;

    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Legacy registration prior to email verification always issued an authenticated session
        // (creating a refresh_tokens record) and provisioned a default Free tier entitlement and subscription.
        // In contrast, new unverified registrations create only the user profile and do not provision
        // refresh tokens, entitlements, or subscriptions until email confirmation succeeds.
        // Therefore, any unconfirmed user with historical refresh tokens, entitlements, or subscriptions
        // is deterministically a legacy user registered before the email verification model,
        // making this backfill independent of deployment wall-clock time.
        migrationBuilder.Sql(BackfillSql);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
    }
}
