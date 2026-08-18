using Customer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer.Domain.Customer>
{
    public void Configure(EntityTypeBuilder<Customer.Domain.Customer> builder)
    {
        builder.ToTable("customers");

        builder.HasKey(c => c.Id);

        builder.Property(c => c.TenantId).IsRequired();
        builder.Property(c => c.DisplayName).HasMaxLength(200).IsRequired();
        builder.Property(c => c.Phone).HasMaxLength(32);
        builder.Property(c => c.NormalizedPhone).HasMaxLength(32);
        builder.Property(c => c.Email).HasMaxLength(320);
        builder.Property(c => c.NormalizedEmail).HasMaxLength(320);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(c => c.ArchivedAt);

        builder.HasAlternateKey(c => new { c.TenantId, c.Id });

        builder.HasIndex(c => new { c.TenantId, c.Status });
        builder.HasIndex(c => new { c.TenantId, c.NormalizedPhone });
        builder.HasIndex(c => new { c.TenantId, c.NormalizedEmail });
        builder.HasIndex(c => new { c.TenantId, c.DisplayName });
    }
}
