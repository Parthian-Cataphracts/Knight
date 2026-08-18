using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderPromotionSnapshotConfiguration : IEntityTypeConfiguration<OrderPromotionSnapshot>
{
    public void Configure(EntityTypeBuilder<OrderPromotionSnapshot> builder)
    {
        builder.ToTable("order_promotion_snapshots");

        builder.HasKey(s => s.Id);

        builder.Property(s => s.TenantId).IsRequired();
        builder.Property(s => s.OrderId).IsRequired();
        builder.Property(s => s.SourcePromotionId);
        builder.Property(s => s.SourceCouponId);
        builder.Property(s => s.PromotionName).HasMaxLength(128).IsRequired();
        builder.Property(s => s.CouponCode).HasMaxLength(64);
        builder.Property(s => s.DiscountType).HasMaxLength(20).IsRequired();
        builder.Property(s => s.DiscountValue).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(s => s.DiscountAmount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasIndex(s => new { s.TenantId, s.OrderId }).IsUnique();
    }
}
