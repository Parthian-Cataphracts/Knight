using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class ProvisioningAndBackups : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DataRetentionDays",
                schema: "control",
                table: "plans",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DataRetentionOverrideDays",
                schema: "control",
                table: "customers",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "provisioning_jobs",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    BaseImageVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    RetainUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provisioning_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provisioning_jobs_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "store_backups",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Location = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_backups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_store_backups_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "provisioning_job_steps",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Mode = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Detail = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CompletedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provisioning_job_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_provisioning_job_steps_provisioning_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "control",
                        principalTable: "provisioning_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_provisioning_job_steps_JobId_Name",
                schema: "control",
                table: "provisioning_job_steps",
                columns: new[] { "JobId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_provisioning_jobs_CustomerId_CreatedAt",
                schema: "control",
                table: "provisioning_jobs",
                columns: new[] { "CustomerId", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_provisioning_jobs_State_UpdatedAt",
                schema: "control",
                table: "provisioning_jobs",
                columns: new[] { "State", "UpdatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_provisioning_jobs_StoreId_IdempotencyKey",
                schema: "control",
                table: "provisioning_jobs",
                columns: new[] { "StoreId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_store_backups_StoreId_StartedAt",
                schema: "control",
                table: "store_backups",
                columns: new[] { "StoreId", "StartedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_store_backups_StoreId_Status_CompletedAt",
                schema: "control",
                table: "store_backups",
                columns: new[] { "StoreId", "Status", "CompletedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provisioning_job_steps",
                schema: "control");

            migrationBuilder.DropTable(
                name: "store_backups",
                schema: "control");

            migrationBuilder.DropTable(
                name: "provisioning_jobs",
                schema: "control");

            migrationBuilder.DropColumn(
                name: "DataRetentionDays",
                schema: "control",
                table: "plans");

            migrationBuilder.DropColumn(
                name: "DataRetentionOverrideDays",
                schema: "control",
                table: "customers");
        }
    }
}
