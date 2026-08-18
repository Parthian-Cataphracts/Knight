using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payment.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class PaymentStatusHistoryConfiguration : IEntityTypeConfiguration<PaymentStatusHistory>
{
    public void Configure(EntityTypeBuilder<PaymentStatusHistory> builder)
    {
        builder.ToTable("payment_status_history");

        builder.HasKey(h => h.Id);

        builder.Property(h => h.TenantId).IsRequired();
        builder.Property(h => h.PaymentId).IsRequired();
        builder.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(h => h.ChangedAt).IsRequired();
        builder.Property(h => h.ActorType).HasMaxLength(50).IsRequired();
        builder.Property(h => h.Reason).HasMaxLength(500);

        builder.HasAlternateKey(h => new { h.TenantId, h.Id });

        builder.HasIndex(h => new { h.TenantId, h.PaymentId, h.ChangedAt });
    }
}
