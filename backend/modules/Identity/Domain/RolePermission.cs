using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

/// <summary>
/// A single permission grant on a <see cref="Role"/>. <see cref="TenantId"/> is
/// denormalized from the owning role specifically so the database can enforce
/// tenant-consistent foreign keys — see docs/architecture/multi-tenancy.md
/// ("Cross-tenant foreign-key protection").
/// </summary>
public sealed class RolePermission : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public Guid RoleId { get; private set; }

    public string PermissionKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private RolePermission()
    {
        PermissionKey = string.Empty;
    }

    private RolePermission(Guid id, Guid tenantId, Guid roleId, string permissionKey, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        RoleId = roleId;
        PermissionKey = permissionKey;
        CreatedAt = createdAt;
    }

    public static RolePermission Create(Guid id, Guid tenantId, Guid roleId, string permissionKey, DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty || roleId == Guid.Empty)
        {
            throw DomainException.Validation("A role permission must belong to a tenant and a role.");
        }

        if (string.IsNullOrWhiteSpace(permissionKey))
        {
            throw DomainException.Validation("Permission key is required.");
        }

        return new RolePermission(id, tenantId, roleId, permissionKey, createdAt);
    }
}
