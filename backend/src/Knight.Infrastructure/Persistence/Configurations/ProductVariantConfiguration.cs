using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class ProductVariantConfiguration : IEntityTypeConfiguration<ProductVariant>
{
    public void Configure(EntityTypeBuilder<ProductVariant> builder)
    {
        builder.ToTable("product_variants");

        builder.HasKey(v => v.Id);

        builder.Property(v => v.TenantId).IsRequired();
        builder.Property(v => v.ProductId).IsRequired();
        builder.Property(v => v.Name).HasMaxLength(150).IsRequired();
        builder.Property(v => v.Sku).HasMaxLength(100);
        builder.Property(v => v.NormalizedSku).HasMaxLength(100);
        builder.Property(v => v.Price).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(v => v.CompareAtPrice).HasColumnType("numeric(18,2)");

        builder.HasAlternateKey(v => new { v.TenantId, v.Id });

        // Partial unique indexes: the database itself guarantees at most one
        // default variant per product and SKU uniqueness only across rows that
        // actually carry a SKU. The default index doubles as the lookup index
        // for listing a product's variants.
        builder.HasIndex(v => new { v.TenantId, v.ProductId })
            .IsUnique()
            .HasFilter("\"IsDefault\" = true")
            .HasDatabaseName("ix_product_variants_tenant_product_default");

        builder.HasIndex(v => new { v.TenantId, v.NormalizedSku })
            .IsUnique()
            .HasFilter("\"NormalizedSku\" IS NOT NULL")
            .HasDatabaseName("ix_product_variants_tenant_normalized_sku");

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(v => new { v.TenantId, v.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
