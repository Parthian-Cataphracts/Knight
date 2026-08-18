using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");

        builder.HasKey(rp => rp.Id);

        builder.Property(rp => rp.TenantId).IsRequired();
        builder.Property(rp => rp.RoleId).IsRequired();
        builder.Property(rp => rp.PermissionKey).HasMaxLength(150).IsRequired();

        builder.HasIndex(rp => new { rp.TenantId, rp.RoleId, rp.PermissionKey }).IsUnique();

        // Tenant-consistent composite FK: a role_permissions row can only ever
        // reference a role that belongs to the SAME tenant — PostgreSQL itself
        // rejects any attempt to connect Role(Tenant A) to a row carrying
        // TenantId = B. See docs/architecture/multi-tenancy.md.
        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(rp => new { rp.TenantId, rp.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Cascade);
    }
}
