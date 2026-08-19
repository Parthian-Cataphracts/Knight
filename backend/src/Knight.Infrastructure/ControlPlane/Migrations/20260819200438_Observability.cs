using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class Observability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsSample",
                schema: "control",
                table: "store_error_events",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "error_groups",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FingerprintVersion = table.Column<int>(type: "integer", nullable: false),
                    Environment = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ExceptionType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    OccurrenceCount = table.Column<long>(type: "bigint", nullable: false),
                    SampleCount = table.Column<int>(type: "integer", nullable: false),
                    FirstSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstSeenVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    LastSeenVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RegressedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcknowledgedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ResolvedInVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_error_groups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "incident_reference_sequences",
                schema: "control",
                columns: table => new
                {
                    Year = table.Column<int>(type: "integer", nullable: false),
                    LastValue = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_reference_sequences", x => x.Year);
                });

            migrationBuilder.CreateTable(
                name: "incidents",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Summary = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: true),
                    ServerId = table.Column<Guid>(type: "uuid", nullable: true),
                    RuleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    OpenedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    OpenedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    MitigatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    RootCause = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incidents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_channels",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Endpoint = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    SecretCipher = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    MinimumSeverity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RuleFilter = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LastDeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_channels", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "incident_events",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    IncidentId = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Type = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: true),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_incident_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_incident_events_incidents_IncidentId",
                        column: x => x.IncidentId,
                        principalSchema: "control",
                        principalTable: "incidents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "notification_deliveries",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RuleKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Subject = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_deliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_notification_deliveries_notification_channels_ChannelId",
                        column: x => x.ChannelId,
                        principalSchema: "control",
                        principalTable: "notification_channels",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_error_groups_CustomerId_Status_LastSeenAt",
                schema: "control",
                table: "error_groups",
                columns: new[] { "CustomerId", "Status", "LastSeenAt" },
                descending: new[] { false, false, true });

            migrationBuilder.CreateIndex(
                name: "IX_error_groups_IncidentId",
                schema: "control",
                table: "error_groups",
                column: "IncidentId");

            migrationBuilder.CreateIndex(
                name: "IX_error_groups_LastSeenAt",
                schema: "control",
                table: "error_groups",
                column: "LastSeenAt");

            migrationBuilder.CreateIndex(
                name: "IX_error_groups_StoreId_Fingerprint_FingerprintVersion",
                schema: "control",
                table: "error_groups",
                columns: new[] { "StoreId", "Fingerprint", "FingerprintVersion" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incident_events_IncidentId_OccurredAt",
                schema: "control",
                table: "incident_events",
                columns: new[] { "IncidentId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_CustomerId",
                schema: "control",
                table: "incidents",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Reference",
                schema: "control",
                table: "incidents",
                column: "Reference",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_incidents_RuleKey_Status",
                schema: "control",
                table: "incidents",
                columns: new[] { "RuleKey", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_Status_OpenedAt",
                schema: "control",
                table: "incidents",
                columns: new[] { "Status", "OpenedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_incidents_StoreId",
                schema: "control",
                table: "incidents",
                column: "StoreId");

            migrationBuilder.CreateIndex(
                name: "IX_notification_channels_CustomerId_IsEnabled",
                schema: "control",
                table: "notification_channels",
                columns: new[] { "CustomerId", "IsEnabled" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_ChannelId_RuleKey_SubjectId_Created~",
                schema: "control",
                table: "notification_deliveries",
                columns: new[] { "ChannelId", "RuleKey", "SubjectId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_CustomerId_ReadAt",
                schema: "control",
                table: "notification_deliveries",
                columns: new[] { "CustomerId", "ReadAt" });

            migrationBuilder.CreateIndex(
                name: "IX_notification_deliveries_Status_NextAttemptAt",
                schema: "control",
                table: "notification_deliveries",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "error_groups",
                schema: "control");

            migrationBuilder.DropTable(
                name: "incident_events",
                schema: "control");

            migrationBuilder.DropTable(
                name: "incident_reference_sequences",
                schema: "control");

            migrationBuilder.DropTable(
                name: "notification_deliveries",
                schema: "control");

            migrationBuilder.DropTable(
                name: "incidents",
                schema: "control");

            migrationBuilder.DropTable(
                name: "notification_channels",
                schema: "control");

            migrationBuilder.DropColumn(
                name: "IsSample",
                schema: "control",
                table: "store_error_events");
        }
    }
}
