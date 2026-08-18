using Knight.Domain.Exceptions;

namespace AccessControl.Domain;

/// <summary>
/// One permission held by one role. The permission is identified by its key
/// rather than a surrogate id: the key is the stable, human-readable contract
/// that endpoints declare, and a join through an opaque id would add a lookup
/// without adding meaning.
/// </summary>
public sealed class RolePermission
{
    public Guid RoleId { get; private set; }

    public string PermissionKey { get; private set; }

    private RolePermission()
    {
        PermissionKey = string.Empty;
    }

    private RolePermission(Guid roleId, string permissionKey)
    {
        RoleId = roleId;
        PermissionKey = permissionKey;
    }

    internal static RolePermission Create(Guid roleId, string permissionKey)
    {
        if (roleId == Guid.Empty)
        {
            throw DomainException.Validation("A role permission must belong to a role.");
        }

        return new RolePermission(roleId, ControlPlanePermissions.Require(permissionKey));
    }
}
