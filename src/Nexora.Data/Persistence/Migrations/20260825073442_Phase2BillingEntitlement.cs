using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861 // Generated EF migration uses provider-required constant arrays.

namespace Nexora.Data.Persistence.Migrations;

/// <inheritdoc />
public partial class Phase2BillingEntitlement : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "idempotency_keys",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                Operation = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Key = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                RequestFingerprint = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                ResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_idempotency_keys", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "outbox_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Type = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                AggregateType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                AggregateId = table.Column<Guid>(type: "uuid", nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_outbox_events", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "plans",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Code = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plans", x => x.Id);
            });

        migrationBuilder.CreateTable(
            name: "plan_prices",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                DurationDays = table.Column<int>(type: "integer", nullable: false),
                InterviewQuota = table.Column<int>(type: "integer", nullable: false),
                IsActive = table.Column<bool>(type: "boolean", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_plan_prices", x => x.Id);
                table.ForeignKey(
                    name: "FK_plan_prices_plans_PlanId",
                    column: x => x.PlanId,
                    principalTable: "plans",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "orders",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanPriceId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanCodeSnapshot = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                AmountMinor = table.Column<long>(type: "bigint", nullable: false),
                Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                DurationDays = table.Column<int>(type: "integer", nullable: false),
                InterviewQuota = table.Column<int>(type: "integer", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                PaymentProvider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ProviderTransactionId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                CheckoutUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_orders", x => x.Id);
                table.ForeignKey(
                    name: "FK_orders_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_orders_plan_prices_PlanPriceId",
                    column: x => x.PlanPriceId,
                    principalTable: "plan_prices",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "payment_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Provider = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                ProviderEventId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_payment_events", x => x.Id);
                table.ForeignKey(
                    name: "FK_payment_events_orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "subscriptions",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_subscriptions", x => x.Id);
                table.ForeignKey(
                    name: "FK_subscriptions_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_subscriptions_orders_OrderId",
                    column: x => x.OrderId,
                    principalTable: "orders",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "entitlements",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                PlanCodeSnapshot = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                InterviewLimit = table.Column<int>(type: "integer", nullable: false),
                Adjustment = table.Column<int>(type: "integer", nullable: false),
                Reserved = table.Column<int>(type: "integer", nullable: false),
                Consumed = table.Column<int>(type: "integer", nullable: false),
                StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                ConcurrencyToken = table.Column<Guid>(type: "uuid", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_entitlements", x => x.Id);
                table.ForeignKey(
                    name: "FK_entitlements_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_entitlements_subscriptions_SubscriptionId",
                    column: x => x.SubscriptionId,
                    principalTable: "subscriptions",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateTable(
            name: "usage_events",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                EntitlementId = table.Column<Guid>(type: "uuid", nullable: false),
                Action = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                Quantity = table.Column<int>(type: "integer", nullable: false),
                SourceType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                SourceId = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_usage_events", x => x.Id);
                table.ForeignKey(
                    name: "FK_usage_events_asp_net_users_UserId",
                    column: x => x.UserId,
                    principalTable: "asp_net_users",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "FK_usage_events_entitlements_EntitlementId",
                    column: x => x.EntitlementId,
                    principalTable: "entitlements",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_entitlements_SubscriptionId",
            table: "entitlements",
            column: "SubscriptionId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_entitlements_UserId_Status_EndsAt",
            table: "entitlements",
            columns: new[] { "UserId", "Status", "EndsAt" });

        migrationBuilder.CreateIndex(
            name: "IX_idempotency_keys_ActorId_Operation_Key",
            table: "idempotency_keys",
            columns: new[] { "ActorId", "Operation", "Key" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_orders_PaymentProvider_ProviderTransactionId",
            table: "orders",
            columns: new[] { "PaymentProvider", "ProviderTransactionId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_orders_PlanPriceId",
            table: "orders",
            column: "PlanPriceId");

        migrationBuilder.CreateIndex(
            name: "IX_orders_UserId_CreatedAt",
            table: "orders",
            columns: new[] { "UserId", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_outbox_events_Status_CreatedAt",
            table: "outbox_events",
            columns: new[] { "Status", "CreatedAt" });

        migrationBuilder.CreateIndex(
            name: "IX_payment_events_OrderId",
            table: "payment_events",
            column: "OrderId");

        migrationBuilder.CreateIndex(
            name: "IX_payment_events_Provider_ProviderEventId",
            table: "payment_events",
            columns: new[] { "Provider", "ProviderEventId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_plan_prices_PlanId_Currency_CreatedAt",
            table: "plan_prices",
            columns: new[] { "PlanId", "Currency", "CreatedAt" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_plans_Code",
            table: "plans",
            column: "Code",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_subscriptions_OrderId",
            table: "subscriptions",
            column: "OrderId",
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_subscriptions_UserId_EndsAt",
            table: "subscriptions",
            columns: new[] { "UserId", "EndsAt" });

        migrationBuilder.CreateIndex(
            name: "IX_usage_events_EntitlementId_Action_SourceId",
            table: "usage_events",
            columns: new[] { "EntitlementId", "Action", "SourceId" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_usage_events_UserId_Action_IdempotencyKey",
            table: "usage_events",
            columns: new[] { "UserId", "Action", "IdempotencyKey" },
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "idempotency_keys");

        migrationBuilder.DropTable(
            name: "outbox_events");

        migrationBuilder.DropTable(
            name: "payment_events");

        migrationBuilder.DropTable(
            name: "usage_events");

        migrationBuilder.DropTable(
            name: "entitlements");

        migrationBuilder.DropTable(
            name: "subscriptions");

        migrationBuilder.DropTable(
            name: "orders");

        migrationBuilder.DropTable(
            name: "plan_prices");

        migrationBuilder.DropTable(
            name: "plans");
    }
}
