namespace Identity.Domain;

public sealed record RoleListItem
{
    public required Role Role { get; init; }
    public required int PermissionCount { get; init; }
    public required int AssignedUserCount { get; init; }
}

/// <summary>
/// Persistence contract for <see cref="Role"/> and its <see cref="RolePermission"/>
/// grants. Deliberately specific rather than a generic repository abstraction.
/// </summary>
public interface IRoleRepository
{
    Task<Role?> GetByIdAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);

    /// <param name="normalizedName">Must already be normalized (uppercase-invariant).</param>
    Task<Role?> GetByNormalizedNameAsync(Guid tenantId, string normalizedName, CancellationToken cancellationToken);

    Task<(IReadOnlyCollection<RoleListItem> Items, long TotalCount)> ListAsync(Guid tenantId, int page, int pageSize, CancellationToken cancellationToken);

    Task AddAsync(Role role, CancellationToken cancellationToken);

    /// <summary>Deletes the role. Callers must have already verified it has no active assignments.</summary>
    Task DeleteAsync(Role role, CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);

    Task<IReadOnlyCollection<string>> GetPermissionKeysAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);

    /// <summary>Atomically replaces the role's permission grants with exactly <paramref name="permissionKeys"/>.</summary>
    Task ReplacePermissionsAsync(Guid tenantId, Guid roleId, IReadOnlyCollection<string> permissionKeys, DateTimeOffset now, CancellationToken cancellationToken);

    Task<int> CountAssignedUsersAsync(Guid tenantId, Guid roleId, CancellationToken cancellationToken);
}
