using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class ControlPlaneCommerce : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "billing_accounts",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BillingEmail = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    TaxId = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_billing_accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "features",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Slug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Category = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsOptional = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresDedicatedInfrastructure = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_features", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "invoice_number_sequences",
                schema: "control",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    LastNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_number_sequences", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "invoices",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Number = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    PeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Subtotal = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Tax = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IssuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BasePriceAmount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "feature_entitlements",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    GrantedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_entitlements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_entitlements_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "feature_prices",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    BillingPeriod = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ValidFrom = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidTo = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_prices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_prices_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Description = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    UnitPrice = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Total = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.Id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "control",
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "payment_records",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvoiceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Amount = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Reference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PaidAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RecordedBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_payment_records", x => x.Id);
                    table.ForeignKey(
                        name: "FK_payment_records_invoices_InvoiceId",
                        column: x => x.InvoiceId,
                        principalSchema: "control",
                        principalTable: "invoices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "plan_features",
                schema: "control",
                columns: table => new
                {
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsIncluded = table.Column<bool>(type: "boolean", nullable: false),
                    IsCustomerToggleable = table.Column<bool>(type: "boolean", nullable: false),
                    PinnedVersionRange = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plan_features", x => new { x.PlanId, x.FeatureId });
                    table.ForeignKey(
                        name: "FK_plan_features_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_plan_features_plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "control",
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodStart = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CurrentPeriodEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscriptions_plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "control",
                        principalTable: "plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "subscription_features",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubscriptionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    EnabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EnabledBy = table.Column<Guid>(type: "uuid", nullable: true),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscription_features", x => x.Id);
                    table.ForeignKey(
                        name: "FK_subscription_features_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_subscription_features_subscriptions_SubscriptionId",
                        column: x => x.SubscriptionId,
                        principalSchema: "control",
                        principalTable: "subscriptions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_billing_accounts_CustomerId",
                schema: "control",
                table: "billing_accounts",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_entitlements_CustomerId_FeatureId_RevokedAt",
                schema: "control",
                table: "feature_entitlements",
                columns: new[] { "CustomerId", "FeatureId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_entitlements_FeatureId",
                schema: "control",
                table: "feature_entitlements",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_feature_prices_FeatureId_PlanId_ValidFrom",
                schema: "control",
                table: "feature_prices",
                columns: new[] { "FeatureId", "PlanId", "ValidFrom" });

            migrationBuilder.CreateIndex(
                name: "IX_features_Slug",
                schema: "control",
                table: "features",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_features_Status",
                schema: "control",
                table: "features",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_InvoiceId",
                schema: "control",
                table: "invoice_lines",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_CustomerId_Status",
                schema: "control",
                table: "invoices",
                columns: new[] { "CustomerId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_Number",
                schema: "control",
                table: "invoices",
                column: "Number",
                unique: true,
                filter: "\"Number\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_payment_records_InvoiceId",
                schema: "control",
                table: "payment_records",
                column: "InvoiceId");

            migrationBuilder.CreateIndex(
                name: "IX_plan_features_FeatureId",
                schema: "control",
                table: "plan_features",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_plans_Key",
                schema: "control",
                table: "plans",
                column: "Key",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscription_features_FeatureId",
                schema: "control",
                table: "subscription_features",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_subscription_features_SubscriptionId_FeatureId",
                schema: "control",
                table: "subscription_features",
                columns: new[] { "SubscriptionId", "FeatureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_CustomerId",
                schema: "control",
                table: "subscriptions",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_PlanId",
                schema: "control",
                table: "subscriptions",
                column: "PlanId");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_Status",
                schema: "control",
                table: "subscriptions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "billing_accounts",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_entitlements",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_prices",
                schema: "control");

            migrationBuilder.DropTable(
                name: "invoice_lines",
                schema: "control");

            migrationBuilder.DropTable(
                name: "invoice_number_sequences",
                schema: "control");

            migrationBuilder.DropTable(
                name: "payment_records",
                schema: "control");

            migrationBuilder.DropTable(
                name: "plan_features",
                schema: "control");

            migrationBuilder.DropTable(
                name: "subscription_features",
                schema: "control");

            migrationBuilder.DropTable(
                name: "invoices",
                schema: "control");

            migrationBuilder.DropTable(
                name: "features",
                schema: "control");

            migrationBuilder.DropTable(
                name: "subscriptions",
                schema: "control");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "control");
        }
    }
}
