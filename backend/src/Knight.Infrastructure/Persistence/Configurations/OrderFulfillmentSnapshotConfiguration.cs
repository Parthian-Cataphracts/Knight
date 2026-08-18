using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

public sealed class OrderFulfillmentSnapshotConfiguration : IEntityTypeConfiguration<OrderFulfillmentSnapshot>
{
    public void Configure(EntityTypeBuilder<OrderFulfillmentSnapshot> builder)
    {
        builder.ToTable("order_fulfillment_snapshots");

        builder.HasKey(f => f.Id);

        builder.HasAlternateKey(f => new { f.TenantId, f.Id });

        builder.HasIndex(f => new { f.TenantId, f.OrderId }).IsUnique();

        builder.Property(f => f.TenantId)
            .IsRequired();

        builder.Property(f => f.OrderId)
            .IsRequired();

        builder.Property(f => f.Method)
            .IsRequired()
            .HasConversion<int>();

        builder.Property(f => f.FulfillmentFee)
            .IsRequired()
            .HasPrecision(18, 2);

        builder.Property(f => f.DeliveryZoneId);

        builder.Property(f => f.DeliveryZoneName)
            .HasMaxLength(100);

        builder.Property(f => f.AddressLine1)
            .HasMaxLength(200);

        builder.Property(f => f.AddressLine2)
            .HasMaxLength(200);

        builder.Property(f => f.City)
            .HasMaxLength(100);

        builder.Property(f => f.PostalCode)
            .HasMaxLength(50);

        builder.Property(f => f.Latitude);
        builder.Property(f => f.Longitude);

        builder.Property(f => f.CreatedAt)
            .IsRequired();

        builder.HasOne<Order>()
            .WithOne(o => o.Fulfillment)
            .HasForeignKey<OrderFulfillmentSnapshot>(f => new { f.TenantId, f.OrderId })
            .HasPrincipalKey<Order>(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
