using Catalog.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class ProductMediaConfiguration : IEntityTypeConfiguration<ProductMedia>
{
    public void Configure(EntityTypeBuilder<ProductMedia> builder)
    {
        builder.ToTable("product_media");

        builder.HasKey(m => m.Id);

        builder.Property(m => m.TenantId).IsRequired();
        builder.Property(m => m.ProductId).IsRequired();
        builder.Property(m => m.StorageKey).HasMaxLength(500).IsRequired();
        builder.Property(m => m.AltText).HasMaxLength(300);

        // Partial unique index: at most one primary media row per product,
        // enforced by the database rather than by application discipline.
        builder.HasIndex(m => new { m.TenantId, m.ProductId })
            .IsUnique()
            .HasFilter("\"IsPrimary\" = true")
            .HasDatabaseName("ix_product_media_tenant_product_primary");

        builder.HasOne<Product>()
            .WithMany()
            .HasForeignKey(m => new { m.TenantId, m.ProductId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
