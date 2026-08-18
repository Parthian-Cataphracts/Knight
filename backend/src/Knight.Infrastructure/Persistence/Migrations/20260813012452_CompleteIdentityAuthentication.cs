using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CompleteIdentityAuthentication : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_users_TenantId_Email",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_SubjectId_SubjectType",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_platform_admins_Email",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                schema: "platform",
                table: "tenant_users",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                schema: "platform",
                table: "tenant_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                schema: "platform",
                table: "tenant_users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                schema: "platform",
                table: "tenant_users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "platform",
                table: "refresh_tokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ConsumedAt",
                schema: "platform",
                table: "refresh_tokens",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "FamilyId",
                schema: "platform",
                table: "refresh_tokens",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedByTokenId",
                schema: "platform",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RevokedReason",
                schema: "platform",
                table: "refresh_tokens",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                schema: "platform",
                table: "refresh_tokens",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedLoginCount",
                schema: "platform",
                table: "platform_admins",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastLoginAt",
                schema: "platform",
                table: "platform_admins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedUntil",
                schema: "platform",
                table: "platform_admins",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "NormalizedEmail",
                schema: "platform",
                table: "platform_admins",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateIndex(
                name: "IX_tenant_users_TenantId_NormalizedEmail",
                schema: "platform",
                table: "tenant_users",
                columns: new[] { "TenantId", "NormalizedEmail" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_FamilyId",
                schema: "platform",
                table: "refresh_tokens",
                column: "FamilyId");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_SubjectType_SubjectId_RevokedAt",
                schema: "platform",
                table: "refresh_tokens",
                columns: new[] { "SubjectType", "SubjectId", "RevokedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_admins_NormalizedEmail",
                schema: "platform",
                table: "platform_admins",
                column: "NormalizedEmail",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_tenant_users_TenantId_NormalizedEmail",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_FamilyId",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_SubjectType_SubjectId_RevokedAt",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropIndex(
                name: "IX_platform_admins_NormalizedEmail",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                schema: "platform",
                table: "tenant_users");

            migrationBuilder.DropColumn(
                name: "ConsumedAt",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "FamilyId",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "ReplacedByTokenId",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "RevokedReason",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "TenantId",
                schema: "platform",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "FailedLoginCount",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "LastLoginAt",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "LockedUntil",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.DropColumn(
                name: "NormalizedEmail",
                schema: "platform",
                table: "platform_admins");

            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "platform",
                table: "refresh_tokens",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(100)",
                oldMaxLength: 100);

            migrationBuilder.CreateIndex(
                name: "IX_tenant_users_TenantId_Email",
                schema: "platform",
                table: "tenant_users",
                columns: new[] { "TenantId", "Email" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_SubjectId_SubjectType",
                schema: "platform",
                table: "refresh_tokens",
                columns: new[] { "SubjectId", "SubjectType" });

            migrationBuilder.CreateIndex(
                name: "IX_platform_admins_Email",
                schema: "platform",
                table: "platform_admins",
                column: "Email",
                unique: true);
        }
    }
}
