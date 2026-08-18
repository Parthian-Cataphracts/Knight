using Identity.Domain;

namespace Identity;

public sealed record CreateStaffInput(string Email, string DisplayName, string InitialPassword, IReadOnlyCollection<Guid> RoleIds);

public sealed record StaffListItem
{
    public required TenantUser User { get; init; }
    public required IReadOnlyCollection<Guid> RoleIds { get; init; }
}

public sealed record StaffListResult(IReadOnlyCollection<StaffListItem> Items, long TotalCount, int Page, int PageSize);

/// <summary>
/// Tenant staff (<see cref="TenantUser"/>) provisioning and administration.
/// Enforces the privilege delegation rule for role assignment, except when
/// called on behalf of an authenticated PlatformAdmin.
/// </summary>
public interface IStaffManagementService
{
    Task<TenantUser> CreateAsync(Guid tenantId, CreateStaffInput input, CancellationToken cancellationToken);

    Task<TenantUser?> GetAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task<StaffListResult> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task<TenantUser> EnableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Disables the account and revokes every active refresh session it has.</summary>
    Task<TenantUser> DisableAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    /// <summary>Clears lockout state without changing the password.</summary>
    Task<TenantUser> UnlockAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);

    Task<IReadOnlyCollection<Guid>> ReplaceRolesAsync(Guid tenantId, Guid userId, IReadOnlyCollection<Guid> roleIds, CancellationToken cancellationToken);

    Task RevokeSessionsAsync(Guid tenantId, Guid userId, CancellationToken cancellationToken);
}
