using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class TenantOrderCounterConfiguration : IEntityTypeConfiguration<TenantOrderCounter>
{
    public void Configure(EntityTypeBuilder<TenantOrderCounter> builder)
    {
        builder.ToTable("tenant_order_counters");

        builder.HasKey(c => c.TenantId);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.NextOrderNumber).IsRequired();
        builder.Ignore(c => c.Id);
    }
}
