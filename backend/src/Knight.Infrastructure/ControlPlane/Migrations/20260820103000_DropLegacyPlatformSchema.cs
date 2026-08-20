using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <summary>
    /// Removes the legacy `platform` schema.
    ///
    /// Phase 8 ported the store-side domains to Django, where each store owns its
    /// own database. Nothing in this solution reads the `platform` schema any
    /// more — the modules, the DbContext and the endpoints that did were deleted
    /// in the same change — so what remains is an unreferenced copy of store data
    /// sitting inside the control plane's database, which is exactly the coupling
    /// ADR 0023 set out to end.
    ///
    /// This is destructive and deliberately so. It was confirmed on 2026-08-20
    /// that the schema holds only development and test data (risks.md, R1); a
    /// deployment carrying real store data must export it into that store's own
    /// database before applying this.
    /// </summary>
    public partial class DropLegacyPlatformSchema : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // IF EXISTS because a database created after the removal never had
            // the schema in the first place, and a fresh install must not fail on
            // the absence of something it was never supposed to have.
            migrationBuilder.Sql("DROP SCHEMA IF EXISTS platform CASCADE;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty. The tables that lived in this schema were
            // defined by EF models that no longer exist in the solution, so there
            // is nothing left to recreate them from. Recovering the schema means
            // restoring the database from a backup taken before the upgrade, and
            // pretending otherwise with a partial rebuild would be worse than
            // saying so.
        }
    }
}
