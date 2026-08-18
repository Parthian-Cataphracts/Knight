using Checkout.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

public sealed class CheckoutIdempotencyRecordConfiguration : IEntityTypeConfiguration<CheckoutIdempotencyRecord>
{
    public void Configure(EntityTypeBuilder<CheckoutIdempotencyRecord> builder)
    {
        builder.ToTable("checkout_idempotency_records");

        builder.HasKey(r => r.Id);

        builder.HasAlternateKey(r => new { r.TenantId, r.Id });

        builder.Property(r => r.TenantId)
            .IsRequired();

        builder.Property(r => r.KeyHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.RequestHash)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(r => r.OrderId);

        builder.Property(r => r.CreatedAt)
            .IsRequired();

        builder.Property(r => r.CompletedAt);

        builder.HasIndex(r => new { r.TenantId, r.KeyHash })
            .IsUnique();
    }
}
