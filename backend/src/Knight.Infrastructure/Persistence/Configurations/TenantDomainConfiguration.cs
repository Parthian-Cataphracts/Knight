using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Tenancy.Domain;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class TenantDomainConfiguration : IEntityTypeConfiguration<TenantDomain>
{
    public void Configure(EntityTypeBuilder<TenantDomain> builder)
    {
        builder.ToTable("tenant_domains");

        builder.HasKey(d => d.Id);

        builder.Property(d => d.Host).HasMaxLength(255).IsRequired();
        builder.Property(d => d.Type).HasConversion<string>().HasMaxLength(20).IsRequired();
        builder.Property(d => d.VerificationStatus).HasConversion<string>().HasMaxLength(20).IsRequired();

        // Global uniqueness: a host can be mapped to exactly one tenant, ever.
        builder.HasIndex(d => d.Host).IsUnique();

        // At most one primary domain per (tenant, purpose).
        builder.HasIndex(d => new { d.TenantId, d.Type })
            .IsUnique()
            .HasFilter("\"IsPrimary\" = true")
            .HasDatabaseName("ix_tenant_domains_tenant_id_type_primary");

        builder.HasOne<Tenant>()
            .WithMany(t => t.Domains)
            .HasForeignKey(d => d.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
