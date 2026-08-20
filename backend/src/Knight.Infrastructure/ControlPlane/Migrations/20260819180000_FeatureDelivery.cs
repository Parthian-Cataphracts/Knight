using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class FeatureDelivery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_configurations",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    ValuesJson = table.Column<string>(type: "jsonb", nullable: false),
                    EncryptedSecretsJson = table.Column<string>(type: "text", nullable: true),
                    SecretNamesJson = table.Column<string>(type: "jsonb", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    AppliedVersion = table.Column<int>(type: "integer", nullable: true),
                    AppliedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_configurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_configurations_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_installation_jobs",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Type = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TargetVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    QueuedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClaimExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RollbackOutcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    RequestedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    Trigger = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TotalStepCount = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_installation_jobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_installation_jobs_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_installations",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    InstalledVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InstalledVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    TargetVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    TargetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreviousVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    CurrentJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureMessage = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RollbackOutcome = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    BlockingReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    InstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UninstalledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DataRetainedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Health = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    LastHealthCheckAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_installations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_installations_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_feature_installations_stores_StoreId",
                        column: x => x.StoreId,
                        principalSchema: "control",
                        principalTable: "stores",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_versions",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PackageReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SigningKeyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ManifestJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    YankedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    YankedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    YankReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_versions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_versions_features_FeatureId",
                        column: x => x.FeatureId,
                        principalSchema: "control",
                        principalTable: "features",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_job_steps",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Output = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_job_steps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_job_steps_feature_installation_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "control",
                        principalTable: "feature_installation_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "feature_dependencies",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DependsOnSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    VersionRangeExpression = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_dependencies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_feature_dependencies_feature_versions_FeatureVersionId",
                        column: x => x.FeatureVersionId,
                        principalSchema: "control",
                        principalTable: "feature_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feature_configurations_StoreId_FeatureId",
                schema: "control",
                table: "feature_configurations",
                columns: new[] { "StoreId", "FeatureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_dependencies_DependsOnSlug",
                schema: "control",
                table: "feature_dependencies",
                column: "DependsOnSlug");

            migrationBuilder.CreateIndex(
                name: "IX_feature_dependencies_FeatureVersionId_DependsOnSlug",
                schema: "control",
                table: "feature_dependencies",
                columns: new[] { "FeatureVersionId", "DependsOnSlug" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_installation_jobs_ClaimExpiresAt",
                schema: "control",
                table: "feature_installation_jobs",
                column: "ClaimExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_feature_installation_jobs_CustomerId_QueuedAt",
                schema: "control",
                table: "feature_installation_jobs",
                columns: new[] { "CustomerId", "QueuedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_feature_installation_jobs_StoreId_IdempotencyKey",
                schema: "control",
                table: "feature_installation_jobs",
                columns: new[] { "StoreId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_installation_jobs_StoreId_State",
                schema: "control",
                table: "feature_installation_jobs",
                columns: new[] { "StoreId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_installations_CustomerId_State",
                schema: "control",
                table: "feature_installations",
                columns: new[] { "CustomerId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_feature_installations_DataRetainedUntil",
                schema: "control",
                table: "feature_installations",
                column: "DataRetainedUntil");

            migrationBuilder.CreateIndex(
                name: "IX_feature_installations_FeatureId",
                schema: "control",
                table: "feature_installations",
                column: "FeatureId");

            migrationBuilder.CreateIndex(
                name: "IX_feature_installations_StoreId_FeatureId",
                schema: "control",
                table: "feature_installations",
                columns: new[] { "StoreId", "FeatureId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_job_steps_JobId_Name",
                schema: "control",
                table: "feature_job_steps",
                columns: new[] { "JobId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_versions_FeatureId_Version",
                schema: "control",
                table: "feature_versions",
                columns: new[] { "FeatureId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_feature_versions_SigningKeyId",
                schema: "control",
                table: "feature_versions",
                column: "SigningKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_feature_versions_Status",
                schema: "control",
                table: "feature_versions",
                column: "Status");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "feature_configurations",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_dependencies",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_installations",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_job_steps",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_versions",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_installation_jobs",
                schema: "control");
        }
    }
}
