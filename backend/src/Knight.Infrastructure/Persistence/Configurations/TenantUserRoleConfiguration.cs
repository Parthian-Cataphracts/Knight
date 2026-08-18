using Identity.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Knight.Infrastructure.Persistence.Configurations;

internal sealed class TenantUserRoleConfiguration : IEntityTypeConfiguration<TenantUserRole>
{
    public void Configure(EntityTypeBuilder<TenantUserRole> builder)
    {
        builder.ToTable("tenant_user_roles");

        builder.HasKey(a => a.Id);

        builder.Property(a => a.TenantId).IsRequired();
        builder.Property(a => a.TenantUserId).IsRequired();
        builder.Property(a => a.RoleId).IsRequired();

        builder.HasIndex(a => new { a.TenantId, a.TenantUserId, a.RoleId }).IsUnique();

        // Both halves of the relationship are tenant-consistent composite FKs —
        // PostgreSQL rejects a row connecting a Tenant A user to a Tenant B
        // role (or vice versa) even if application code has a bug. See
        // docs/architecture/multi-tenancy.md.
        builder.HasOne<TenantUser>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.TenantUserId })
            .HasPrincipalKey(u => new { u.TenantId, u.Id })
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasOne<Role>()
            .WithMany()
            .HasForeignKey(a => new { a.TenantId, a.RoleId })
            .HasPrincipalKey(r => new { r.TenantId, r.Id })
            .OnDelete(DeleteBehavior.Restrict);
    }
}
