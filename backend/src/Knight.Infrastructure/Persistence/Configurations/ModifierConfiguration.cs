using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class ModifierConfiguration : IEntityTypeConfiguration<Modifier>
{
    public void Configure(EntityTypeBuilder<Modifier> builder)
    {
        builder.ToTable("modifiers");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.ModifierGroupId).IsRequired();
        builder.Property(m => m.Name).HasMaxLength(150).IsRequired();
        builder.Property(m => m.PriceDelta).HasColumnType("numeric(18,2)").IsRequired();

        builder.HasIndex(m => new { m.TenantId, m.ModifierGroupId });

        builder.HasAlternateKey(m => new { m.TenantId, m.Id });

        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.ModifierGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
