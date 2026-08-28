using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class ExternalServiceArchitecture : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Architecture",
                schema: "control",
                table: "feature_installations",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "InProcess");

            migrationBuilder.AddColumn<string>(
                name: "Architecture",
                schema: "control",
                table: "feature_installation_jobs",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "InProcess");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Architecture",
                schema: "control",
                table: "feature_installations");

            migrationBuilder.DropColumn(
                name: "Architecture",
                schema: "control",
                table: "feature_installation_jobs");
        }
    }
}
