using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Promotions.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class CouponConfiguration : IEntityTypeConfiguration<Coupon>
{
    public void Configure(EntityTypeBuilder<Coupon> builder)
    {
        builder.ToTable("coupons");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.PromotionId).IsRequired();
        builder.Property(c => c.Code).HasMaxLength(64).IsRequired();
        builder.Property(c => c.NormalizedCode).HasMaxLength(64).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.UsageLimitTotal);
        builder.Property(c => c.StartsAt);
        builder.Property(c => c.EndsAt);
        builder.Property(c => c.CreatedAt).IsRequired();
        // Nullable by design: AuditableEntity leaves UpdatedAt null until the first
        // mutation, and Coupon.Create never sets it.
        builder.Property(c => c.UpdatedAt);
        builder.Property(c => c.ArchivedAt);

        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.HasIndex(c => new { c.TenantId, c.NormalizedCode }).IsUnique();
        builder.HasIndex(c => new { c.TenantId, c.PromotionId });
        builder.HasIndex(c => new { c.TenantId, c.Status });

        builder.HasOne<Promotion>()
            .WithMany()
            .HasForeignKey(c => new { c.TenantId, c.PromotionId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
