using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.ControlPlane.Migrations
{
    /// <inheritdoc />
    public partial class CustomerNotes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "customer_notes",
                schema: "control",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customer_notes", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_customer_notes_CustomerId_CreatedAt",
                schema: "control",
                table: "customer_notes",
                columns: new[] { "CustomerId", "CreatedAt" },
                descending: new[] { false, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "customer_notes",
                schema: "control");
        }
    }
}
