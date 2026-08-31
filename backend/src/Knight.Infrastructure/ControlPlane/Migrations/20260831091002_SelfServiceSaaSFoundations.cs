using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class SelfServiceSaaSFoundations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "CancelAtPeriodEnd",
                schema: "control",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                schema: "control",
                table: "subscriptions",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProviderSubscriptionId",
                schema: "control",
                table: "subscriptions",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "AttemptCount",
                schema: "control",
                table: "provisioning_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "FailureClass",
                schema: "control",
                table: "provisioning_jobs",
                type: "character varying(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPubliclyPurchasable",
                schema: "control",
                table: "plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "checkout_sessions",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Interval = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SelectedFeatureIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ProviderSessionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_sessions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "platform_billing_transactions",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderTransactionId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RefundedAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_platform_billing_transactions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_Provider_ProviderSubscriptionId",
                schema: "control",
                table: "subscriptions",
                columns: new[] { "Provider", "ProviderSubscriptionId" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sessions_CustomerId",
                schema: "control",
                table: "checkout_sessions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sessions_Provider_ProviderSessionId",
                schema: "control",
                table: "checkout_sessions",
                columns: new[] { "Provider", "ProviderSessionId" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_sessions_SubscriptionId",
                schema: "control",
                table: "checkout_sessions",
                column: "SubscriptionId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_billing_transactions_CustomerId",
                schema: "control",
                table: "platform_billing_transactions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_platform_billing_transactions_IdempotencyKey",
                schema: "control",
                table: "platform_billing_transactions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_platform_billing_transactions_Provider_ProviderTransactionId",
                schema: "control",
                table: "platform_billing_transactions",
                columns: new[] { "Provider", "ProviderTransactionId" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_billing_transactions_SubscriptionId",
                schema: "control",
                table: "platform_billing_transactions",
                column: "SubscriptionId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "checkout_sessions",
                schema: "control");

            migrationBuilder.DropTable(
                name: "platform_billing_transactions",
                schema: "control");

            migrationBuilder.DropIndex(
                name: "IX_subscriptions_Provider_ProviderSubscriptionId",
                schema: "control",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "CancelAtPeriodEnd",
                schema: "control",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "Provider",
                schema: "control",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "ProviderSubscriptionId",
                schema: "control",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "AttemptCount",
                schema: "control",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "FailureClass",
                schema: "control",
                table: "provisioning_jobs");

            migrationBuilder.DropColumn(
                name: "IsPubliclyPurchasable",
                schema: "control",
                table: "plans");
        }
    }
}
