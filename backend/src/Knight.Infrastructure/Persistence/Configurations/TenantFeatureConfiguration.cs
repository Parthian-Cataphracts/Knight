using FeatureManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class TenantFeatureConfiguration : IEntityTypeConfiguration<TenantFeature>
{
    public void Configure(EntityTypeBuilder<TenantFeature> builder)
    {
        builder.ToTable("tenant_features");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.TenantId).IsRequired();
        builder.Property(f => f.FeatureKey).HasMaxLength(100).IsRequired();

        builder.HasIndex(f => new { f.TenantId, f.FeatureKey }).IsUnique();
    }
}
