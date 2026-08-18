using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemModifierConfiguration : IEntityTypeConfiguration<OrderItemModifier>
{
    public void Configure(EntityTypeBuilder<OrderItemModifier> builder)
    {
        builder.ToTable("order_item_modifiers");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.OrderItemId).IsRequired();
        builder.Property(m => m.SourceModifierGroupId).IsRequired();
        builder.Property(m => m.ModifierGroupName).HasMaxLength(150).IsRequired();
        builder.Property(m => m.SourceModifierId).IsRequired();
        builder.Property(m => m.ModifierName).HasMaxLength(150).IsRequired();
        builder.Property(m => m.UnitPriceDelta).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(m => m.DisplayOrder).IsRequired();

        builder.HasAlternateKey(m => new { m.TenantId, m.Id });

        builder.HasIndex(m => new { m.TenantId, m.OrderItemId });
    }
}
