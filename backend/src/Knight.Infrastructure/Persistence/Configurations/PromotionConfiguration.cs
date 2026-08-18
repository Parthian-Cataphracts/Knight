using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Promotions.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class PromotionConfiguration : IEntityTypeConfiguration<Promotion>
{
    public void Configure(EntityTypeBuilder<Promotion> builder)
    {
        builder.ToTable("promotions");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.Name).HasMaxLength(128).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(500);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.DiscountType).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.DiscountValue).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(p => p.MinimumSubtotal).HasColumnType("numeric(18,2)");
        builder.Property(p => p.MaximumDiscountAmount).HasColumnType("numeric(18,2)");
        builder.Property(p => p.StartsAt);
        builder.Property(p => p.EndsAt);
        builder.Property(p => p.RequiresCoupon).IsRequired();
        builder.Property(p => p.Priority).IsRequired().HasDefaultValue(0);
        builder.Property(p => p.CreatedAt).IsRequired();
        // Nullable by design: AuditableEntity leaves UpdatedAt null until the first
        // mutation, and Promotion.Create never sets it.
        builder.Property(p => p.UpdatedAt);
        builder.Property(p => p.ArchivedAt);

        builder.HasAlternateKey(p => new { p.TenantId, p.Id });

        builder.HasIndex(p => new { p.TenantId, p.Status });
        builder.HasIndex(p => new { p.TenantId, p.CreatedAt });
    }
}
