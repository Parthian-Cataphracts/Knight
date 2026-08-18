using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");

        builder.HasKey(o => o.Id);

        builder.Property(o => o.TenantId).IsRequired();
        builder.Property(o => o.OrderNumber).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(o => o.Currency).HasMaxLength(3).IsRequired();
        builder.Property(o => o.Subtotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(o => o.DiscountTotal).HasColumnType("numeric(18,2)").IsRequired().HasDefaultValue(0m);
        builder.Property(o => o.FulfillmentFee).HasColumnType("numeric(18,2)").IsRequired().HasDefaultValue(0m);
        builder.Property(o => o.Total).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(o => o.CancellationReason).HasMaxLength(500);

        builder.Property(o => o.Version)
            .IsConcurrencyToken()
            .IsRequired()
            .HasDefaultValue(1);

        builder.HasAlternateKey(o => new { o.TenantId, o.Id });

        builder.HasIndex(o => new { o.TenantId, o.OrderNumber }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.CreatedAt });
        builder.HasIndex(o => new { o.TenantId, o.Status, o.CreatedAt });

        builder.HasMany(o => o.Items)
            .WithOne()
            .HasForeignKey(i => new { i.TenantId, i.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(o => o.StatusHistory)
            .WithOne()
            .HasForeignKey(h => new { h.TenantId, h.OrderId })
            .HasPrincipalKey(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne(o => o.Promotion)
            .WithOne()
            .HasForeignKey<OrderPromotionSnapshot>(s => new { s.TenantId, s.OrderId })
            .HasPrincipalKey<Order>(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
