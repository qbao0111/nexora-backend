using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional
#pragma warning disable CA1861 // Prefer static readonly fields for constant arrays

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class InterviewFreeContinuation : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.InsertData(
            table: "feature_definitions",
            columns: new[] { "Id", "Code", "CreatedAt", "Description", "IsActive", "Name", "SortOrder", "UpdatedAt" },
            values: new object[] { new Guid("20000000-0000-0000-0000-000000000007"), "interview_question_limit", new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Số câu hỏi tối đa trong mỗi phiên phỏng vấn", true, "Giới hạn câu hỏi phỏng vấn", 6, new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) });

        migrationBuilder.InsertData(
            table: "plan_price_features",
            columns: new[] { "Id", "CreatedAt", "FeatureDefinitionId", "IsEnabled", "Limit", "PlanPriceId", "UpdatedAt" },
            values: new object[,]
            {
                { new Guid("30000000-0000-0000-0000-000000000018"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("20000000-0000-0000-0000-000000000007"), true, 3, new Guid("10000000-0000-0000-0000-000000000001"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                { new Guid("30000000-0000-0000-0000-000000000019"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("20000000-0000-0000-0000-000000000007"), true, 6, new Guid("10000000-0000-0000-0000-000000000002"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                { new Guid("30000000-0000-0000-0000-000000000020"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("20000000-0000-0000-0000-000000000007"), true, 8, new Guid("10000000-0000-0000-0000-000000000003"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                { new Guid("30000000-0000-0000-0000-000000000021"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), new Guid("20000000-0000-0000-0000-000000000007"), true, 10, new Guid("10000000-0000-0000-0000-000000000004"), new DateTimeOffset(new DateTime(2026, 8, 25, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
            });

        migrationBuilder.Sql("""
            WITH entitlement_prices AS (
                SELECT e."Id" AS entitlement_id,
                       COALESCE(o."PlanPriceId", (
                           SELECT pp0."Id"
                           FROM "plan_prices" AS pp0
                           INNER JOIN "plans" AS p0 ON p0."Id" = pp0."PlanId"
                           WHERE p0."Code" = e."PlanCodeSnapshot" AND pp0."IsActive"
                           ORDER BY pp0."CreatedAt" DESC
                           LIMIT 1
                       )) AS plan_price_id,
                       e."CreatedAt",
                       e."UpdatedAt"
                FROM "entitlements" AS e
                LEFT JOIN "subscriptions" AS s ON s."Id" = e."SubscriptionId"
                LEFT JOIN "orders" AS o ON o."Id" = s."OrderId"
            )
            INSERT INTO "entitlement_features"
                ("Id", "EntitlementId", "FeatureDefinitionId", "FeatureCode", "IsEnabled", "Limit", "Reserved", "Consumed", "Adjustment", "ConcurrencyToken", "CreatedAt", "UpdatedAt")
            SELECT md5(ep.entitlement_id::text || ':interview_question_limit')::uuid,
                   ep.entitlement_id,
                   ppf."FeatureDefinitionId",
                   'interview_question_limit',
                   ppf."IsEnabled",
                   ppf."Limit",
                   0,
                   0,
                   0,
                   md5(ep.entitlement_id::text || ':interview_question_limit:concurrency')::uuid,
                   ep."CreatedAt",
                   ep."UpdatedAt"
            FROM entitlement_prices AS ep
            INNER JOIN "plan_price_features" AS ppf ON ppf."PlanPriceId" = ep.plan_price_id
            WHERE ppf."FeatureDefinitionId" = '20000000-0000-0000-0000-000000000007'::uuid
              AND NOT EXISTS (
                  SELECT 1
                  FROM "entitlement_features" AS existing
                  WHERE existing."EntitlementId" = ep.entitlement_id
                    AND existing."FeatureDefinitionId" = ppf."FeatureDefinitionId"
              )
            ON CONFLICT ("EntitlementId", "FeatureDefinitionId") DO NOTHING;
            """);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DELETE FROM "entitlement_features"
            WHERE "FeatureDefinitionId" = '20000000-0000-0000-0000-000000000007'::uuid;
            """);

        migrationBuilder.DeleteData(
            table: "plan_price_features",
            keyColumn: "Id",
            keyValue: new Guid("30000000-0000-0000-0000-000000000018"));

        migrationBuilder.DeleteData(
            table: "plan_price_features",
            keyColumn: "Id",
            keyValue: new Guid("30000000-0000-0000-0000-000000000019"));

        migrationBuilder.DeleteData(
            table: "plan_price_features",
            keyColumn: "Id",
            keyValue: new Guid("30000000-0000-0000-0000-000000000020"));

        migrationBuilder.DeleteData(
            table: "plan_price_features",
            keyColumn: "Id",
            keyValue: new Guid("30000000-0000-0000-0000-000000000021"));

        migrationBuilder.DeleteData(
            table: "feature_definitions",
            keyColumn: "Id",
            keyValue: new Guid("20000000-0000-0000-0000-000000000007"));
    }
}
