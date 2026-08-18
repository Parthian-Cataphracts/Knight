using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class RoleConfiguration : IEntityTypeConfiguration<Role>
{
    public void Configure(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");

        builder.HasKey(r => r.Id);

        builder.Property(r => r.TenantId).IsRequired();
        builder.Property(r => r.Name).HasMaxLength(100).IsRequired();
        builder.Property(r => r.NormalizedName).HasMaxLength(100).IsRequired();

        builder.HasIndex(r => new { r.TenantId, r.NormalizedName }).IsUnique();

        // Lets RolePermission and TenantUserRole declare tenant-consistent
        // composite foreign keys against (TenantId, Id) — see
        // docs/architecture/multi-tenancy.md.
        builder.HasAlternateKey(r => new { r.TenantId, r.Id });
    }
}
