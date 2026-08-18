using Identity.Domain;

namespace Identity;

public sealed record CreateRoleInput(string Name, IReadOnlyCollection<string> PermissionKeys);

public sealed record RoleListResult(IReadOnlyCollection<RoleListItem> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Tenant role and role-permission management. Enforces the privilege
/// delegation rule (see <see cref="Identity.Authorization.DelegationGuard"/>)
/// for every operation that grants permissions, except when called on behalf
/// of an authenticated PlatformAdmin.
/// </summary>
public interface IRoleManagementService
{
    Task<Role> CreateAsync(Guid tenantId, CreateRoleInput input, CancellationToken cancellationToken);

    Task<Role?> GetAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetPermissionKeysAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);

    Task<RoleListResult> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task<Role> RenameAsync(Guid tenantId, Guid roleId, string name, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> SetPermissionsAsync(Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissionKeys, CancellationToken cancellationToken);

    /// <summary>Throws <see cref="Knight.Application.Exceptions.ConflictException"/> if the role has active assignments.</summary>
    Task DeleteAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);
}
