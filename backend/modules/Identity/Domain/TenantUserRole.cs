using Knight.Domain.Common;
using Knight.Domain.Exceptions;

namespace Identity.Domain;

/// <summary>
/// Assigns one <see cref="Role"/> to one <see cref="TenantUser"/>. Both must
/// belong to the same tenant — <see cref="TenantId"/> is denormalized here
/// specifically so the database can enforce that with real composite foreign
/// keys rather than relying on application discipline alone — see
/// docs/architecture/multi-tenancy.md.
/// </summary>
public sealed class TenantUserRole : Entity, ITenantScoped
{
    public Guid TenantId { get; private set; }

    public Guid TenantUserId { get; private set; }

    public Guid RoleId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    private TenantUserRole()
    {
    }

    private TenantUserRole(Guid id, Guid tenantId, Guid tenantUserId, Guid roleId, DateTimeOffset createdAt)
        : base(id)
    {
        TenantId = tenantId;
        TenantUserId = tenantUserId;
        RoleId = roleId;
        CreatedAt = createdAt;
    }

    public static TenantUserRole Create(Guid id, Guid tenantId, Guid tenantUserId, Guid roleId, DateTimeOffset createdAt)
    {
        if (tenantId == Guid.Empty || tenantUserId == Guid.Empty || roleId == Guid.Empty)
        {
            throw DomainException.Validation("A staff role assignment must reference a tenant, a user, and a role.");
        }

        return new TenantUserRole(id, tenantId, tenantUserId, roleId, createdAt);
    }
}
