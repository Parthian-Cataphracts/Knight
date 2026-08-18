using FeatureManagement.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class FeatureDefinitionConfiguration : IEntityTypeConfiguration<FeatureDefinition>
{
    public void Configure(EntityTypeBuilder<FeatureDefinition> builder)
    {
        builder.ToTable("feature_definitions");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.Key).HasMaxLength(100).IsRequired();
        builder.Property(f => f.Name).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(1000);

        builder.HasIndex(f => f.Key).IsUnique();
    }
}
