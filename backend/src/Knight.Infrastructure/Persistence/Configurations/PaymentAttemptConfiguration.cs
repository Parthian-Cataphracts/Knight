using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payment.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class PaymentAttemptConfiguration : IEntityTypeConfiguration<PaymentAttempt>
{
    public void Configure(EntityTypeBuilder<PaymentAttempt> builder)
    {
        builder.ToTable("payment_attempts");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.PaymentId).IsRequired();
        builder.Property(a => a.AttemptNumber).IsRequired();
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(a => a.ProviderKey).HasMaxLength(50);
        builder.Property(a => a.ProviderReference).HasMaxLength(100);
        builder.Property(a => a.FailureCode).HasMaxLength(50);
        builder.Property(a => a.FailureMessage).HasMaxLength(500);
        builder.Property(a => a.CreatedAt).IsRequired();

        builder.HasAlternateKey(a => new { a.TenantId, a.Id });

        builder.HasIndex(a => new { a.TenantId, a.PaymentId, a.AttemptNumber }).IsUnique();

        builder.HasIndex(a => new { a.TenantId, a.ProviderKey, a.ProviderReference })
            .IsUnique()
            .HasFilter("\"ProviderReference\" IS NOT NULL");
    }
}
