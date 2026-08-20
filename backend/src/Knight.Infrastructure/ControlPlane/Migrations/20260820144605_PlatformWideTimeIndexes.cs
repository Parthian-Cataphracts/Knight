using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <summary>
    /// Time-ordered indexes for the dashboard views that name no store.
    ///
    /// Every index on these three tables led with StoreId, which serves a store's
    /// own detail page and cannot serve the platform-wide feed a staff user opens
    /// first. Those listings were sequential scans plus a top-N sort over the
    /// whole table — 18ms across 238k log rows, and linear in the row count from
    /// there. Measured before and after in docs/phase-10-verification.md.
    /// </summary>
    public partial class PlatformWideTimeIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_store_log_entries_Timestamp",
                schema: "control",
                table: "store_log_entries",
                column: "Timestamp",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_store_events_OccurredAt",
                schema: "control",
                table: "store_events",
                column: "OccurredAt",
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_store_error_events_OccurredAt_Id",
                schema: "control",
                table: "store_error_events",
                columns: new[] { "OccurredAt", "Id" },
                descending: new[] { true, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_store_log_entries_Timestamp",
                schema: "control",
                table: "store_log_entries");

            migrationBuilder.DropIndex(
                name: "IX_store_events_OccurredAt",
                schema: "control",
                table: "store_events");

            migrationBuilder.DropIndex(
                name: "IX_store_error_events_OccurredAt_Id",
                schema: "control",
                table: "store_error_events");
        }
    }
}
