using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class ProductModifierGroupConfiguration : IEntityTypeConfiguration<ProductModifierGroup>
{
    public void Configure(EntityTypeBuilder<ProductModifierGroup> builder)
    {
        builder.ToTable("product_modifier_groups");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.ProductId).IsRequired();
        builder.Property(a => a.ModifierGroupId).IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.ProductId, a.ModifierGroupId }).IsUnique();

        // Deleting the product's own row takes its assignments with it; deleting a
        // modifier group that is still assigned must fail so the application layer
        // can report the conflict.
        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<ModifierGroup>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.ModifierGroupId })
            .HasPrincipalKey(g => new { g.TenantId, g.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
