using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AccountInvitations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActivationExpiresAt",
                schema: "control",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationTokenHash",
                schema: "control",
                table: "users",
                type: "character varying(200)",
                maxLength: 200,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_ActivationTokenHash",
                schema: "control",
                table: "users",
                column: "ActivationTokenHash");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_users_ActivationTokenHash",
                schema: "control",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActivationExpiresAt",
                schema: "control",
                table: "users");

            migrationBuilder.DropColumn(
                name: "ActivationTokenHash",
                schema: "control",
                table: "users");
        }
    }
}
