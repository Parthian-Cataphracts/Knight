using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Ordering.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class OrderPartySnapshotConfiguration : IEntityTypeConfiguration<OrderPartySnapshot>
{
    public void Configure(EntityTypeBuilder<OrderPartySnapshot> builder)
    {
        builder.ToTable("order_party_snapshots");

        builder.HasKey(p => p.Id);

        builder.Property(p => p.TenantId).IsRequired();
        builder.Property(p => p.OrderId).IsRequired();
        builder.Property(p => p.SourceCustomerId);
        builder.Property(p => p.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Phone).HasMaxLength(32);
        builder.Property(p => p.Email).HasMaxLength(320);
        builder.Property(p => p.CreatedAt).IsRequired();

        builder.HasAlternateKey(p => new { p.TenantId, p.Id });

        // Exactly one party snapshot per order guaranteed by unique index
        builder.HasIndex(p => new { p.TenantId, p.OrderId }).IsUnique();

        builder.HasOne<Order>()
            .WithOne(o => o.Party)
            .HasForeignKey<OrderPartySnapshot>(p => new { p.TenantId, p.OrderId })
            .HasPrincipalKey<Order>(o => new { o.TenantId, o.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
