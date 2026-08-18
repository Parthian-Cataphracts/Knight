using Delivery.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

public sealed class DeliveryZoneConfiguration : IEntityTypeConfiguration<DeliveryZone>
{
    public void Configure(EntityTypeBuilder<DeliveryZone> builder)
    {
        builder.ToTable("delivery_zones");

        builder.HasKey(z => z.Id);

        builder.HasAlternateKey(z => new { z.TenantId, z.Id });

        builder.Property(z => z.TenantId)
            .IsRequired();

        builder.Property(z => z.Name)
            .IsRequired()
            .HasMaxLength(100);

        builder.Property(z => z.Fee)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(z => z.MinimumOrderSubtotal)
            .HasPrecision(18, 2);

        builder.Property(z => z.Status)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(z => z.DisplayOrder)
            .IsRequired()
            .HasDefaultValue(0);

        builder.Property(z => z.CreatedAt)
            .IsRequired();

        builder.Property(z => z.UpdatedAt);
        builder.Property(z => z.ArchivedAt);

        builder.HasIndex(z => new { z.TenantId, z.Status });
        builder.HasIndex(z => new { z.TenantId, z.DisplayOrder });
    }
}
