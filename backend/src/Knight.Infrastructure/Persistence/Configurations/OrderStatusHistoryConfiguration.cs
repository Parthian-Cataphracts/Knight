using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        builder.ToTable("order_status_history");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();
        builder.Property(h => h.OrderId).IsRequired();
        builder.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(h => h.ChangedAt).IsRequired();
        builder.Property(h => h.ChangedByPrincipalType).HasConversion<string>().HasMaxLength(30);
        builder.Property(h => h.Reason).HasMaxLength(500);

        builder.HasAlternateKey(h => new { h.TenantId, h.Id });

        builder.HasIndex(h => new { h.TenantId, h.OrderId, h.ChangedAt });
    }
}
