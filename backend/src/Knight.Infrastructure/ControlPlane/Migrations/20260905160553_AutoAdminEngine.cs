using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class AutoAdminEngine : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "auto_admin_settings",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Autonomy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_auto_admin_settings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_jobs",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Topic = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Autonomy = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "content_drafts",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: false),
                    GeneratorName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_drafts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_content_drafts_content_jobs_ContentJobId",
                        column: x => x.ContentJobId,
                        principalSchema: "control",
                        principalTable: "content_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "content_publications",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentJobId = table.Column<Guid>(type: "uuid", nullable: false),
                    ChannelKey = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    Detail = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                    ExternalReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    PublisherName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_content_publications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_content_publications_content_jobs_ContentJobId",
                        column: x => x.ContentJobId,
                        principalSchema: "control",
                        principalTable: "content_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_auto_admin_settings_CustomerId",
                schema: "control",
                table: "auto_admin_settings",
                column: "CustomerId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_content_drafts_ContentJobId",
                schema: "control",
                table: "content_drafts",
                column: "ContentJobId");

            migrationBuilder.CreateIndex(
                name: "IX_content_jobs_CustomerId_CreatedAt",
                schema: "control",
                table: "content_jobs",
                columns: new[] { "CustomerId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_content_publications_ContentJobId",
                schema: "control",
                table: "content_publications",
                column: "ContentJobId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "auto_admin_settings",
                schema: "control");

            migrationBuilder.DropTable(
                name: "content_drafts",
                schema: "control");

            migrationBuilder.DropTable(
                name: "content_publications",
                schema: "control");

            migrationBuilder.DropTable(
                name: "content_jobs",
                schema: "control");
        }
    }
}
