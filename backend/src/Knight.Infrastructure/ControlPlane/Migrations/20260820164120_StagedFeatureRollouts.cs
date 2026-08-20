using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class StagedFeatureRollouts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "feature_rollouts",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureId = table.Column<Guid>(type: "uuid", nullable: false),
                    FeatureSlug = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    FeatureVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FailureThreshold = table.Column<int>(type: "integer", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    HaltReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_feature_rollouts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "rollout_waves",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RolloutId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollout_waves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rollout_waves_feature_rollouts_RolloutId",
                        column: x => x.RolloutId,
                        principalSchema: "control",
                        principalTable: "feature_rollouts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "rollout_targets",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: false),
                    StoreId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Detail = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    DispatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_rollout_targets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_rollout_targets_rollout_waves_WaveId",
                        column: x => x.WaveId,
                        principalSchema: "control",
                        principalTable: "rollout_waves",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_feature_rollouts_CreatedAt",
                schema: "control",
                table: "feature_rollouts",
                column: "CreatedAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_feature_rollouts_FeatureId_active",
                schema: "control",
                table: "feature_rollouts",
                column: "FeatureId",
                unique: true,
                filter: "\"State\" in ('Planned', 'InProgress', 'Halted')");

            migrationBuilder.CreateIndex(
                name: "IX_feature_rollouts_State_CreatedAt",
                schema: "control",
                table: "feature_rollouts",
                columns: new[] { "State", "CreatedAt" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_rollout_targets_JobId",
                schema: "control",
                table: "rollout_targets",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "IX_rollout_targets_WaveId_StoreId",
                schema: "control",
                table: "rollout_targets",
                columns: new[] { "WaveId", "StoreId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_rollout_waves_RolloutId_Ordinal",
                schema: "control",
                table: "rollout_waves",
                columns: new[] { "RolloutId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "rollout_targets",
                schema: "control");

            migrationBuilder.DropTable(
                name: "rollout_waves",
                schema: "control");

            migrationBuilder.DropTable(
                name: "feature_rollouts",
                schema: "control");
        }
    }
}
