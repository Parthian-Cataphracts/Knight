using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Payment.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment.Domain.Payment>
{
    public void Configure(EntityTypeBuilder<Payment.Domain.Payment> builder)
    {
        builder.ToTable("payments");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.OrderId).IsRequired();
        builder.Property(p => p.Amount).HasColumnType("numeric(18,2)").IsRequired();
        builder.Property(p => p.Currency).HasMaxLength(10).IsRequired();
        builder.Property(p => p.Method).HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(p => p.CreatedAt).IsRequired();
        builder.Property(p => p.UpdatedAt).IsRequired();

        builder.Property(p => p.Version)
            .IsConcurrencyToken()
            .IsRequired()
            .HasDefaultValue(1);

        builder.HasAlternateKey(p => new { p.TenantId, p.Id });

        builder.HasIndex(p => new { p.TenantId, p.OrderId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.CreatedAt });
        builder.HasIndex(p => new { p.TenantId, p.Status, p.CreatedAt });

        builder.HasMany(p => p.Attempts)
            .WithOne()
            .HasForeignKey(a => new { a.TenantId, a.PaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(p => p.StatusHistories)
            .WithOne()
            .HasForeignKey(h => new { h.TenantId, h.PaymentId })
            .HasPrincipalKey(p => new { p.TenantId, p.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
