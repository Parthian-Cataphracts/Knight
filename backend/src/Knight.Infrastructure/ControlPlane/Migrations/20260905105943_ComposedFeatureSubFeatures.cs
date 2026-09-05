using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class ComposedFeatureSubFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentFeatureId",
                schema: "control",
                table: "features",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_features_ParentFeatureId",
                schema: "control",
                table: "features",
                column: "ParentFeatureId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_features_ParentFeatureId",
                schema: "control",
                table: "features");

            migrationBuilder.DropColumn(
                name: "ParentFeatureId",
                schema: "control",
                table: "features");
        }
    }
}
