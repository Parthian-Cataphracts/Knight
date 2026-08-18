using Delivery.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

public sealed class TenantDeliverySettingsConfiguration : IEntityTypeConfiguration<TenantDeliverySettings>
{
    public void Configure(EntityTypeBuilder<TenantDeliverySettings> builder)
    {
        builder.ToTable("tenant_delivery_settings");

        builder.HasKey(s => s.TenantId);

        builder.Property(s => s.TenantId)
            .IsRequired();

        builder.Property(s => s.IsAcceptingDeliveryOrders)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(s => s.DefaultMinimumOrderSubtotal)
            .HasPrecision(18, 2);

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        builder.Property(s => s.UpdatedAt);
    }
}
