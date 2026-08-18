using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Promotions.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class CouponRedemptionConfiguration : IEntityTypeConfiguration<CouponRedemption>
{
    public void Configure(EntityTypeBuilder<CouponRedemption> builder)
    {
        builder.ToTable("coupon_redemptions");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.CouponId).IsRequired();
        builder.Property(r => r.OrderId).IsRequired();
        builder.Property(r => r.RedeemedAt).IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.CouponId });
        builder.HasIndex(r => new { r.TenantId, r.OrderId }).IsUnique();

        builder.HasOne<Coupon>()
            .WithMany()
            .HasForeignKey(r => new { r.TenantId, r.CouponId })
            .HasPrincipalKey(c => new { c.TenantId, c.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
