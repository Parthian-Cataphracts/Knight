using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Knight.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// Corrective migration. <c>AddPromotionsAndCoupons</c> created
    /// <c>promotions.UpdatedAt</c> and <c>coupons.UpdatedAt</c> as NOT NULL, which
    /// contradicts both its own model snapshot and the <c>AuditableEntity</c>
    /// contract those aggregates inherit: <c>UpdatedAt</c> stays null until the
    /// first mutation, and neither <c>Promotion.Create</c> nor <c>Coupon.Create</c>
    /// assigns it. Inserting a newly created promotion or coupon therefore violated
    /// the NOT NULL constraint. Relaxing the columns to nullable aligns the
    /// PostgreSQL schema with the model every other auditable entity uses.
    /// </summary>
    public partial class FixPromotionCouponUpdatedAtNullability : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "platform",
                table: "promotions",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "platform",
                table: "coupons",
                type: "timestamp with time zone",
                nullable: true,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "platform",
                table: "promotions",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "UpdatedAt",
                schema: "platform",
                table: "coupons",
                type: "timestamp with time zone",
                nullable: false,
                defaultValue: new DateTimeOffset(new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)),
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);
        }
    }
}
