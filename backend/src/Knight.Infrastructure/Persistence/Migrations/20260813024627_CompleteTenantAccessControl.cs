using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteTenantAccessControl : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_roles_TenantId_Name",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "RoleIds",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropColumn(
                name: "PermissionKeys",
                schema: "platform",
                table: "roles");

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "platform",
                table: "roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(200)",
                oldMaxLength: 200);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedName",
                schema: "platform",
                table: "roles",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_tenant_users_TenantId_Id",
                schema: "platform",
                table: "tenant_users",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.AddUniqueConstraint(
                name: "AK_roles_TenantId_Id",
                schema: "platform",
                table: "roles",
                columns: new[] { "TenantId", "Id" });

            migrationBuilder.CreateTable(
                name: "role_permissions",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionKey = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_role_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_role_permissions_roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "platform",
                        principalTable: "roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "tenant_user_roles",
                schema: "platform",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    TenantUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tenant_user_roles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_tenant_user_roles_roles_TenantId_RoleId",
                        columns: x => new { x.TenantId, x.RoleId },
                        principalSchema: "platform",
                        principalTable: "roles",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_tenant_user_roles_tenant_users_TenantId_TenantUserId",
                        columns: x => new { x.TenantId, x.TenantUserId },
                        principalSchema: "platform",
                        principalTable: "tenant_users",
                        principalColumns: new[] { "TenantId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_NormalizedName",
                schema: "platform",
                table: "roles",
                columns: new[] { "TenantId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_role_permissions_TenantId_RoleId_PermissionKey",
                schema: "platform",
                table: "role_permissions",
                columns: new[] { "TenantId", "RoleId", "PermissionKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_user_roles_TenantId_RoleId",
                schema: "platform",
                table: "tenant_user_roles",
                columns: new[] { "TenantId", "RoleId" });

            migrationBuilder.CreateIndex(
                name: "IX_tenant_user_roles_TenantId_TenantUserId_RoleId",
                schema: "platform",
                table: "tenant_user_roles",
                columns: new[] { "TenantId", "TenantUserId", "RoleId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "role_permissions",
                schema: "platform");

            migrationBuilder.DropTable(
                name: "tenant_user_roles",
                schema: "platform");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_tenant_users_TenantId_Id",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_roles_TenantId_Id",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropIndex(
                name: "IX_roles_TenantId_NormalizedName",
                schema: "platform",
                table: "roles");

            migrationBuilder.DropColumn(
                name: "NormalizedName",
                schema: "platform",
                table: "roles");

            migrationBuilder.AddColumn<Guid[]>(
                name: "RoleIds",
                schema: "platform",
                table: "tenant_users",
                type: "uuid[]",
                nullable: false,
                defaultValue: new Guid[0]);

            migrationBuilder.AlterColumn<string>(
                name: "Name",
                schema: "platform",
                table: "roles",
                type: "character varying(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.AddColumn<string[]>(
                name: "PermissionKeys",
                schema: "platform",
                table: "roles",
                type: "character varying(150)[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.CreateIndex(
                name: "IX_roles_TenantId_Name",
                schema: "platform",
                table: "roles",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }
    }
}
