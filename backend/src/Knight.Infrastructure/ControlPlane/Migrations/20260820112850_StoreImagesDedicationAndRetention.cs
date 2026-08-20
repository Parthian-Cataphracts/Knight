using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class StoreImagesDedicationAndRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MutualTlsThumbprint",
                schema: "control",
                table: "stores",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DedicatedCustomerId",
                schema: "control",
                table: "servers",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "store_images",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    StoreVersion = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PackageReference = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ArtifactSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    Signature = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    SigningKeyId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PublishedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    YankedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    YankReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_store_images", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_servers_DedicatedCustomerId",
                schema: "control",
                table: "servers",
                column: "DedicatedCustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_store_images_SigningKeyId",
                schema: "control",
                table: "store_images",
                column: "SigningKeyId");

            migrationBuilder.CreateIndex(
                name: "IX_store_images_Version",
                schema: "control",
                table: "store_images",
                column: "Version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "store_images",
                schema: "control");

            migrationBuilder.DropIndex(
                name: "IX_servers_DedicatedCustomerId",
                schema: "control",
                table: "servers");

            migrationBuilder.DropColumn(
                name: "MutualTlsThumbprint",
                schema: "control",
                table: "stores");

            migrationBuilder.DropColumn(
                name: "DedicatedCustomerId",
                schema: "control",
                table: "servers");
        }
    }
}
