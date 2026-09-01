using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class SelfServiceRegistration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EmailVerified",
                schema: "control",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EmailVerified",
                schema: "control",
                table: "users");
        }
    }
}
