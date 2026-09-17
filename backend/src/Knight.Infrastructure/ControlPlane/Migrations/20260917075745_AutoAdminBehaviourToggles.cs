using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AutoAdminBehaviourToggles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoReplyEnabled",
                schema: "control",
                table: "auto_admin_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "BoostEnabled",
                schema: "control",
                table: "auto_admin_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoReplyEnabled",
                schema: "control",
                table: "auto_admin_settings");

            migrationBuilder.DropColumn(
                name: "BoostEnabled",
                schema: "control",
                table: "auto_admin_settings");
        }
    }
}
