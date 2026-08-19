using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class StoreIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DomainVerificationIssuedAt",
                schema: "control",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainVerificationMethod",
                schema: "control",
                table: "stores",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DomainVerificationToken",
                schema: "control",
                table: "stores",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DomainVerifiedAt",
                schema: "control",
                table: "stores",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "store_deployments",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PreviousVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    DeployedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DetectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Notes = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_deployments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_deployments_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_error_events",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StoreVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ExceptionType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    HttpMethod = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    StackTrace = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Context = table.Column<string>(type: "jsonb", nullable: true),
                    ErrorGroupId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_error_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_error_events_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_events",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TraceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_events_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_health_checks",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ResponseTimeMs = table.Column<int>(type: "integer", nullable: true),
                    ReportedVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Dependencies = table.Column<string>(type: "jsonb", nullable: true),
                    ReportedFeatures = table.Column<string>(type: "jsonb", nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_health_checks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_health_checks_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_log_entries",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Timestamp = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Service = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StoreVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RequestId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Exception = table.Column<string>(type: "character varying(20000)", maxLength: 20000, nullable: true),
                    Attributes = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_log_entries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_log_entries_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_store_deployments_StoreId_DeployedAt",
                schema: "control",
                table: "store_deployments",
                columns: new[] { "StoreId", "DeployedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_store_error_events_ErrorGroupId",
                schema: "control",
                table: "store_error_events",
                column: "ErrorGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_store_error_events_StoreId_OccurredAt",
                schema: "control",
                table: "store_error_events",
                columns: new[] { "StoreId", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_store_events_StoreId_OccurredAt",
                schema: "control",
                table: "store_events",
                columns: new[] { "StoreId", "OccurredAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_store_events_Type",
                schema: "control",
                table: "store_events",
                column: "Type");

            migrationBuilder.CreateIndex(
                name: "IX_store_health_checks_CheckedAt",
                schema: "control",
                table: "store_health_checks",
                column: "CheckedAt");

            migrationBuilder.CreateIndex(
                name: "IX_store_health_checks_StoreId_CheckedAt",
                schema: "control",
                table: "store_health_checks",
                columns: new[] { "StoreId", "CheckedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_store_log_entries_StoreId_Level",
                schema: "control",
                table: "store_log_entries",
                columns: new[] { "StoreId", "Level" });

            migrationBuilder.CreateIndex(
                name: "IX_store_log_entries_StoreId_Timestamp",
                schema: "control",
                table: "store_log_entries",
                columns: new[] { "StoreId", "Timestamp" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_deployments",
                schema: "control");

            migrationBuilder.DropTable(
                name: "store_error_events",
                schema: "control");

            migrationBuilder.DropTable(
                name: "store_events",
                schema: "control");

            migrationBuilder.DropTable(
                name: "store_health_checks",
                schema: "control");

            migrationBuilder.DropTable(
                name: "store_log_entries",
                schema: "control");

            migrationBuilder.DropColumn(
                name: "DomainVerificationIssuedAt",
                schema: "control",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "DomainVerificationMethod",
                schema: "control",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "DomainVerificationToken",
                schema: "control",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "DomainVerifiedAt",
                schema: "control",
                table: "stores");
        }
    }
}
