using Fulfillment.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

public sealed class TenantFulfillmentSettingsConfiguration : IEntityTypeConfiguration<TenantFulfillmentSettings>
{
    public void Configure(EntityTypeBuilder<TenantFulfillmentSettings> builder)
    {
        builder.ToTable("tenant_fulfillment_settings");

        builder.HasKey(s => s.TenantId);

        builder.Property(s => s.TenantId)
            .IsRequired();

        builder.Property(s => s.PickupEnabled)
            .IsRequired()
            .HasDefaultValue(true);

        builder.Property(s => s.CreatedAt)
            .IsRequired();

        builder.Property(s => s.UpdatedAt);
    }
}
