using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.ToTable("order_items");

        builder.HasKey(i => i.Id);

        builder.Property(i => i.TenantId).IsRequired();
        builder.Property(i => i.OrderId).IsRequired();
        builder.Property(i => i.SourceProductId).IsRequired();
        builder.Property(i => i.ProductName).HasMaxLength(150).IsRequired();
        builder.Property(i => i.VariantName).HasMaxLength(150);
        builder.Property(i => i.UnitBasePrice).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.Quantity).IsRequired();
        builder.Property(i => i.UnitModifierTotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.UnitPrice).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.LineTotal).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(i => i.DisplayOrder).IsRequired();

        builder.HasAlternateKey(i => new { i.TenantId, i.Id });

        builder.HasIndex(i => new { i.TenantId, i.OrderId });

        builder.HasMany(i => i.Modifiers)
            .WithOne()
            .HasForeignKey(m => new { m.TenantId, m.OrderItemId })
            .HasPrincipalKey(i => new { i.TenantId, i.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
